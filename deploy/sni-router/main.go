// sni-router shares one TLS port between several backends without terminating TLS.
//
// It reads the ClientHello, picks a backend by SNI (connections without SNI, such as clients
// that connect to a bare IP, go to the default backend), optionally prepends a PROXY protocol v1
// header so the backend still sees the real client address, and then splices the two sockets.
//
//	CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o sni-router .
//	sni-router -config /etc/sni-router/config.json
package main

import (
	"bufio"
	"encoding/binary"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"os/signal"
	"strings"
	"sync"
	"syscall"
	"time"
)

type Backend struct {
	Addr          string `json:"addr"`
	ProxyProtocol bool   `json:"proxy_protocol"`
}

type Config struct {
	Listen  []string           `json:"listen"`
	Routes  map[string]Backend `json:"routes"` // lower-case SNI -> backend
	Default Backend            `json:"default"`
}

const helloTimeout = 10 * time.Second

func main() {
	cfgPath := flag.String("config", "/etc/sni-router/config.json", "config file")
	check := flag.Bool("check", false, "validate the config and exit")
	flag.Parse()

	cfg, err := load(*cfgPath)
	if err != nil {
		log.Fatalf("config: %v", err)
	}
	if *check {
		fmt.Println("config ok")
		return
	}

	var listeners []net.Listener
	for _, addr := range cfg.Listen {
		ln, err := net.Listen("tcp", addr)
		if err != nil {
			log.Fatalf("listen %s: %v", addr, err)
		}
		listeners = append(listeners, ln)
		log.Printf("listening on %s", addr)
		go serve(ln, cfg)
	}

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig
	for _, ln := range listeners {
		ln.Close()
	}
	log.Printf("stopped")
}

func load(path string) (*Config, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var c Config
	if err := json.Unmarshal(b, &c); err != nil {
		return nil, err
	}
	if len(c.Listen) == 0 || c.Default.Addr == "" {
		return nil, errors.New("listen and default.addr are required")
	}
	routes := map[string]Backend{}
	for k, v := range c.Routes {
		routes[strings.ToLower(strings.TrimSuffix(k, "."))] = v
	}
	c.Routes = routes
	return &c, nil
}

func serve(ln net.Listener, cfg *Config) {
	for {
		c, err := ln.Accept()
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				return
			}
			log.Printf("accept: %v", err)
			time.Sleep(100 * time.Millisecond)
			continue
		}
		go handle(c, cfg)
	}
}

func handle(client net.Conn, cfg *Config) {
	defer client.Close()
	client.SetReadDeadline(time.Now().Add(helloTimeout))
	br := bufio.NewReaderSize(client, 16384+5)
	sni, err := peekSNI(br)
	client.SetReadDeadline(time.Time{})
	if err != nil && !errors.Is(err, errNotTLS) {
		log.Printf("%s: client hello: %v", client.RemoteAddr(), err)
		return
	}

	be, ok := cfg.Routes[strings.ToLower(sni)]
	if !ok {
		be = cfg.Default
	}
	upstream, err := net.DialTimeout("tcp", be.Addr, 5*time.Second)
	if err != nil {
		log.Printf("%s sni=%q -> %s: %v", client.RemoteAddr(), sni, be.Addr, err)
		return
	}
	defer upstream.Close()
	if sni != "" {
		log.Printf("%s sni=%q -> %s", client.RemoteAddr(), sni, be.Addr)
	}

	if be.ProxyProtocol {
		if _, err := io.WriteString(upstream, proxyHeader(client.RemoteAddr(), client.LocalAddr())); err != nil {
			return
		}
	}

	var wg sync.WaitGroup
	wg.Add(2)
	go func() { defer wg.Done(); pipe(upstream, br) }()     // client -> backend (including the peeked hello)
	go func() { defer wg.Done(); pipe(client, upstream) }() // backend -> client
	wg.Wait()
}

func pipe(dst net.Conn, src io.Reader) {
	io.Copy(dst, src)
	if tc, ok := dst.(*net.TCPConn); ok {
		tc.CloseWrite()
	} else {
		dst.Close()
	}
}

// proxyHeader builds a PROXY protocol v1 line. IPv4-mapped IPv6 addresses are reported as TCP4.
func proxyHeader(src, dst net.Addr) string {
	s, d := src.(*net.TCPAddr), dst.(*net.TCPAddr)
	if s4, d4 := s.IP.To4(), d.IP.To4(); s4 != nil && d4 != nil {
		return fmt.Sprintf("PROXY TCP4 %s %s %d %d\r\n", s4, d4, s.Port, d.Port)
	}
	return fmt.Sprintf("PROXY TCP6 %s %s %d %d\r\n", s.IP, d.IP, s.Port, d.Port)
}

var errNotTLS = errors.New("not a TLS handshake")

// peekSNI returns the server_name from a ClientHello without consuming it from br.
// It returns "" with a nil error when the hello carries no SNI (e.g. a client connecting to an IP).
func peekSNI(br *bufio.Reader) (string, error) {
	hdr, err := br.Peek(5)
	if err != nil {
		return "", err
	}
	if hdr[0] != 0x16 || hdr[1] != 0x03 {
		return "", errNotTLS
	}
	recLen := int(binary.BigEndian.Uint16(hdr[3:5]))
	if recLen < 4 || recLen > 16384 {
		return "", fmt.Errorf("bad record length %d", recLen)
	}
	rec, err := br.Peek(5 + recLen)
	if err != nil {
		return "", err
	}
	p := rec[5:]
	if p[0] != 0x01 { // ClientHello
		return "", errors.New("not a ClientHello")
	}
	hsLen := int(p[1])<<16 | int(p[2])<<8 | int(p[3])
	p = p[4:]
	if hsLen > len(p) {
		// A ClientHello split across records: route without SNI rather than fail.
		return "", nil
	}
	p = p[:hsLen]

	skip := func(n int) bool {
		if len(p) < n {
			return false
		}
		p = p[n:]
		return true
	}
	vec := func(lenBytes int) bool {
		if len(p) < lenBytes {
			return false
		}
		n := 0
		for i := 0; i < lenBytes; i++ {
			n = n<<8 | int(p[i])
		}
		return skip(lenBytes + n)
	}
	// version(2) random(32) session_id<1> cipher_suites<2> compression<1>
	if !skip(2+32) || !vec(1) || !vec(2) || !vec(1) {
		return "", errors.New("truncated ClientHello")
	}
	if len(p) < 2 {
		return "", nil // no extensions
	}
	extLen := int(binary.BigEndian.Uint16(p))
	p = p[2:]
	if extLen > len(p) {
		return "", errors.New("truncated extensions")
	}
	p = p[:extLen]
	for len(p) >= 4 {
		typ := binary.BigEndian.Uint16(p)
		l := int(binary.BigEndian.Uint16(p[2:]))
		p = p[4:]
		if l > len(p) {
			return "", errors.New("truncated extension")
		}
		if typ == 0 { // server_name
			e := p[:l]
			if len(e) < 2 {
				return "", nil
			}
			e = e[2:] // list length
			for len(e) >= 3 {
				nameType := e[0]
				n := int(binary.BigEndian.Uint16(e[1:]))
				e = e[3:]
				if n > len(e) {
					break
				}
				if nameType == 0 {
					return string(e[:n]), nil
				}
				e = e[n:]
			}
			return "", nil
		}
		p = p[l:]
	}
	return "", nil
}
