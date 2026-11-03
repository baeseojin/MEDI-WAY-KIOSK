"use strict";
(() => {
 const token = new URLSearchParams(location.hash.slice(1)).get("t") || "";
 const $ = id => document.getElementById(id);
 const svgNS = "http://www.w3.org/2000/svg";
 let busy = false, ended = false, expiry = 0, zoom = 0, controller;
 const levels = [1, 1.5, 2, 3];
 let currentRoute = null, legIndex = 0;
 function element(name, attrs, text) {
  const node = document.createElementNS(svgNS, name);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
  if (text !== undefined) node.textContent = text;
  return node;
 }
 function clearMap() { $("guide").hidden = true; $("drawing").replaceChildren(); $("steps").textContent = ""; }
 function end(message) {
  ended = true; controller?.abort(); clearMap(); document.body.dataset.ended = "true";
  document.body.dataset.error = "true"; $("destination").textContent = "안내가 종료되었습니다";
  $("ticket").textContent = ""; $("distance").textContent = ""; $("expires").textContent = ""; $("message").textContent = message;
 }
 function setZoom(index) { zoom = Math.max(0, Math.min(levels.length - 1, index)); $("map").dataset.zoom = String(levels[zoom]); }
 function draw(route) {
  currentRoute = route;
  const legs = route.legs?.length ? route.legs : [route];
  legIndex = Math.min(legIndex, legs.length - 1);
  $("next-floor").hidden = legs.length < 2;
  $("next-floor").textContent = `${legs[(legIndex + 1) % legs.length].map.name} 보기`;
  $("floor-caption").textContent = `${legs[legIndex].map.name} ${route.transferNotice || ""}`;
  drawLeg(legs[legIndex]);
 }
 function drawLeg(route) {
  const svg = $("drawing"); svg.replaceChildren(); svg.setAttribute("viewBox", `0 0 ${route.map.width} ${route.map.height}`);
  svg.setAttribute("aria-label", `현재 위치에서 ${route.destinationName}까지의 이동 경로`);
  const nodes = new Map(route.map.nodes.map(n => [n.id, n]));
  for (const zone of route.map.zones) {
   svg.append(element("rect", { x: zone.x, y: zone.y, width: zone.width, height: zone.height, rx: 8, class: `zone ${zone.kind}` }));
   svg.append(element("text", { x: zone.x + zone.width / 2, y: zone.y + zone.height / 2 + 6, "text-anchor": "middle" }, zone.name));
  }
  for (const link of route.map.links) {
   const a = nodes.get(link.fromNodeId), b = nodes.get(link.toNodeId);
   svg.append(element("line", { x1: a.x, y1: a.y, x2: b.x, y2: b.y, class: link.isClosed ? "closed" : "corridor" }));
  }
  for (const n of route.map.nodes) svg.append(element("circle", { cx: n.x, cy: n.y, r: 4, class: "node" }));
  const path = route.points.map((p, i) => `${i ? "L" : "M"}${p.x},${p.y}`).join(" ");
  svg.append(element("path", { d: path, class: "route", pathLength: 1 }));
  const first = route.points[0], last = route.points[route.points.length - 1];
  for (const [point, cls, label] of [[first, "start", first.kind === "Elevator" ? "엘리베이터 하차" : "현재 위치"], [last, "end", last.kind === "Elevator" ? "엘리베이터" : "진료실 입구"]]) {
   svg.append(element("circle", { cx: point.x, cy: point.y, r: 11, class: cls }));
   svg.append(element("text", { x: point.x, y: point.y + 31, "text-anchor": "middle" }, label));
  }
  $("steps").textContent = route.points.map(p => p.name).join(" → "); setZoom(0);
  $("guide").hidden = false;
 }
 async function refresh() {
  if (busy || ended) return;
  if (expiry && Date.now() >= expiry) { end("이 안내가 만료되었습니다. 키오스크에서 QR을 다시 받아 주세요."); return; }
  busy = true; $("refresh").disabled = true; controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10000);
  try {
   const response = await fetch("../api/mobile-guide", { headers: { Authorization: `Bearer ${token}` }, cache: "no-store", credentials: "omit", referrerPolicy: "no-referrer", signal: controller.signal });
   const data = await response.json(); if (ended) return;
   if (!response.ok) {
    if (response.status === 401 || response.status === 410) { end(data.message || "안내가 종료되었습니다."); return; }
    throw new Error(response.status >= 500 ? "길 안내에 연결하지 못했습니다. 잠시 후 다시 확인해 주세요." : data.message || "경로를 확인할 수 없습니다.");
   }
   expiry = Date.parse(data.expiresAt);
   if (!Number.isFinite(expiry) || Date.now() >= expiry) { end("이 안내가 만료되었습니다."); return; }
   draw(data.route); document.body.dataset.error = "false";
   $("destination").textContent = data.route.destinationName;
   const status = { Waiting: "대기 중", Called: "호출됨", InService: "진료 중" }[data.visitStatus] || "";
   $("ticket").textContent = `${String(data.queueNumber).padStart(3, "0")}번 · ${status}`;
   $("distance").textContent = `이동 거리 ${data.route.distanceMeters.toFixed(1).replace(/\.0$/, "")}m`;
   $("expires").textContent = `안내 유효시간: ${new Date(expiry).toLocaleString("ko-KR")}`;
   $("message").textContent = data.route.map.links.some(l => l.isClosed) ? "일부 통로가 폐쇄되어 있습니다. 표시된 경로를 따라 이동해 주세요." : "청록색 경로를 따라 진료실 입구로 이동해 주세요.";
  } catch (error) {
   if (!ended) { clearMap(); document.body.dataset.error = "true"; $("message").textContent = error.name === "AbortError" ? "연결을 확인하지 못했습니다. 다시 확인해 주세요." : error.message; }
  } finally { clearTimeout(timeout); busy = false; $("refresh").disabled = false; }
 }
 $("refresh").addEventListener("click", refresh);
 $("zoom-in").addEventListener("click", () => setZoom(zoom + 1));
 $("zoom-out").addEventListener("click", () => setZoom(zoom - 1));
 $("fit").addEventListener("click", () => setZoom(0));
 $("next-floor").addEventListener("click", () => { if (currentRoute?.legs?.length > 1) { legIndex = (legIndex + 1) % currentRoute.legs.length; draw(currentRoute); } });
 document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
 setInterval(() => { if (expiry && Date.now() >= expiry && !ended) end("이 안내가 만료되었습니다. 키오스크에서 QR을 다시 받아 주세요."); }, 1000);
 setInterval(refresh, 30000);
 if (!/^[0-9a-f]{64}$/.test(token)) end("키오스크에서 발급한 QR을 스캔해 주세요."); else refresh();
})();
