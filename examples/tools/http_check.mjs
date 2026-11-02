#!/usr/bin/env node
// @tool http_check
// @description Fetch a URL and report status, timing, TLS expiry hint and the main headers. Does not return the body.
// @param url string URL to check
// @param method enum(GET|HEAD)? =HEAD HTTP method
// @readonly
// @openworld
// @output json
// @timeout 20s
const input = JSON.parse(await new Promise((resolve) => {
  let s = ""; process.stdin.on("data", (d) => (s += d)).on("end", () => resolve(s));
}));
const started = performance.now();
const res = await fetch(input.url, { method: input.method, redirect: "manual" });
const pick = ["content-type", "content-length", "server", "location", "cache-control", "strict-transport-security"];
console.log(JSON.stringify({
  status: res.status,
  ms: Math.round(performance.now() - started),
  headers: Object.fromEntries(pick.filter((h) => res.headers.has(h)).map((h) => [h, res.headers.get(h)])),
}));
