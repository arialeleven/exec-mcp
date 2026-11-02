package main

import (
	"bufio"
	"bytes"
	"crypto/tls"
	"io"
	"net"
	"strings"
	"testing"
	"time"
)

// clientHello captures the first flight of a real crypto/tls client.
func clientHello(t *testing.T, serverName string) []byte {
	t.Helper()
	c, s := net.Pipe()
	go func() {
		tc := tls.Client(c, &tls.Config{ServerName: serverName, InsecureSkipVerify: serverName == ""})
		tc.SetDeadline(time.Now().Add(time.Second))
		tc.Handshake() // fails: nobody answers; we only need what it sent
		c.Close()
	}()
	s.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 16384)
	n, _ := io.ReadAtLeast(s, buf, 5)
	// read the rest of the record
	recLen := int(buf[3])<<8 | int(buf[4])
	for n < 5+recLen {
		m, err := s.Read(buf[n:])
		if err != nil {
			break
		}
		n += m
	}
	s.Close()
	return buf[:n]
}

func TestPeekSNI(t *testing.T) {
	for _, name := range []string{"example.com", "203-0-113-10.sslip.io", ""} {
		hello := clientHello(t, name)
		br := bufio.NewReaderSize(bytes.NewReader(hello), 16384+5)
		got, err := peekSNI(br)
		if err != nil {
			t.Fatalf("%q: %v", name, err)
		}
		if got != name {
			t.Fatalf("want %q, got %q", name, got)
		}
		// Peeking must not consume anything.
		rest, _ := io.ReadAll(br)
		if !bytes.Equal(rest, hello) {
			t.Fatalf("%q: peeked bytes were consumed", name)
		}
	}
}

func TestNotTLS(t *testing.T) {
	br := bufio.NewReader(strings.NewReader("GET / HTTP/1.1\r\n\r\n"))
	if _, err := peekSNI(br); err != errNotTLS {
		t.Fatalf("want errNotTLS, got %v", err)
	}
}

func TestProxyHeader(t *testing.T) {
	v4 := proxyHeader(&net.TCPAddr{IP: net.ParseIP("160.79.106.25"), Port: 51000}, &net.TCPAddr{IP: net.ParseIP("::ffff:10.0.0.1"), Port: 443})
	if v4 != "PROXY TCP4 160.79.106.25 10.0.0.1 51000 443\r\n" {
		t.Fatalf("v4: %q", v4)
	}
	v6 := proxyHeader(&net.TCPAddr{IP: net.ParseIP("2001:db8::1"), Port: 1}, &net.TCPAddr{IP: net.ParseIP("2001:db8::2"), Port: 443})
	if v6 != "PROXY TCP6 2001:db8::1 2001:db8::2 1 443\r\n" {
		t.Fatalf("v6: %q", v6)
	}
}

// End to end: a route with PROXY protocol and the default route without it.
func TestRouting(t *testing.T) {
	backend := func() (net.Listener, chan string) {
		ln, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			t.Fatal(err)
		}
		got := make(chan string, 1)
		go func() {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			c.SetReadDeadline(time.Now().Add(2 * time.Second))
			buf := make([]byte, 64)
			n, _ := c.Read(buf)
			got <- string(buf[:n])
			c.Close()
		}()
		return ln, got
	}
	named, namedGot := backend()
	def, defGot := backend()
	defer named.Close()
	defer def.Close()

	cfg := &Config{
		Routes:  map[string]Backend{"svc.test": {Addr: named.Addr().String(), ProxyProtocol: true}},
		Default: Backend{Addr: def.Addr().String()},
	}
	front, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer front.Close()
	go serve(front, cfg)

	send := func(serverName string) {
		c, err := net.Dial("tcp", front.Addr().String())
		if err != nil {
			t.Fatal(err)
		}
		c.Write(clientHello(t, serverName))
		time.Sleep(100 * time.Millisecond)
		c.Close()
	}

	send("svc.test")
	if got := <-namedGot; !strings.HasPrefix(got, "PROXY TCP4 127.0.0.1 127.0.0.1 ") {
		t.Fatalf("named backend got %q", got)
	}
	send("") // like a client connecting to a bare IP
	if got := <-defGot; !strings.HasPrefix(got, "\x16\x03") {
		t.Fatalf("default backend should get the raw ClientHello, got %q", got[:min(len(got), 16)])
	}
}
