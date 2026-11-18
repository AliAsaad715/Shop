// Shared by the two live pages (task R4-08): signing in, calling the API with the token, and the SignalR connection.
// Every value from the server reaches the page through textContent, never as HTML.

const tokenKey = "shop.accessToken";

// The event names the hubs call; Shop.Modules.Notifications.Realtime.OrderUpdates on the server
export const OrderPlaced = "orderPlaced";
export const OrderStatusChanged = "orderStatusChanged";

// sessionStorage keeps the token for this tab only, so an admin tab and a customer tab can sit side by side
export function readToken() {
  try {
    return sessionStorage.getItem(tokenKey);
  } catch {
    return null;
  }
}

export function clearToken() {
  try {
    sessionStorage.removeItem(tokenKey);
  } catch {
    // Storage is blocked: the token only lived in memory
  }
}

function saveToken(token) {
  try {
    sessionStorage.setItem(tokenKey, token);
  } catch {
    // Storage is blocked: signing in again after a reload is the only cost
  }
}

// The token's own claims, read for display and to tell when it expires; the server checks it on every call
export function claims(token) {
  const part = token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
  const json = decodeURIComponent(Array.from(atob(part), c => "%" + c.charCodeAt(0).toString(16).padStart(2, "0")).join(""));
  const values = JSON.parse(json);
  return { email: values.email, roles: [].concat(values.role ?? []), expiresAt: new Date(values.exp * 1000) };
}

export function isUsable(token, role) {
  if (!token) return false;
  try {
    const { roles, expiresAt } = claims(token);
    return roles.includes(role) && expiresAt > new Date();
  } catch {
    return false;
  }
}

export class ApiError extends Error {
  constructor(status, problem) {
    super(problem?.detail ?? problem?.title ?? `The request failed (${status}).`);
    this.status = status;
    this.problem = problem;
  }
}

export async function api(path, { method = "GET", body, token } = {}) {
  const headers = { Accept: "application/json" };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";

  const response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const isJson = (response.headers.get("Content-Type") ?? "").includes("json");
  const data = isJson ? await response.json() : null;
  if (!response.ok) throw new ApiError(response.status, data);
  return data;
}

export async function signIn(email, password) {
  const { accessToken } = await api("/api/v1/auth/login", { method: "POST", body: { email, password } });
  saveToken(accessToken);
  return accessToken;
}

export async function register(email, password) {
  await api("/api/v1/auth/register", { method: "POST", body: { email, password } });
  return signIn(email, password);
}

// The field errors of a 422, or the problem's own message
export function describe(error) {
  const fields = error.problem?.errors;
  return fields ? Object.values(fields).flat().join(" ") : error.message;
}

/**
 * Connects to a hub with the token and keeps the page told about the connection. SignalR reconnects by itself after
 * a drop (immediately, then after 2, 10 and 30 seconds); events sent while it was away are lost, so onReconnected
 * reloads what the page shows. The server closes the connection when the token expires.
 */
export async function connectLive({ hub, token, handlers, onState, onReconnected }) {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(hub, { accessTokenFactory: () => token })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  for (const [name, handler] of Object.entries(handlers)) connection.on(name, handler);
  connection.onreconnecting(() => onState("reconnecting"));
  connection.onreconnected(async () => {
    onState("live");
    await onReconnected();
  });
  connection.onclose(error => onState("offline", error));

  await connection.start();
  onState("live");
  return connection;
}

// A live event changes what the page shows only if it is newer: events can arrive out of order, and an admin's own
// change is already on screen from the API's answer when its event arrives
export function isNewer(event, shown) {
  return shown === undefined || event.version > shown.version;
}

export function el(tag, attributes = {}, ...children) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (name.startsWith("on")) node.addEventListener(name.slice(2), value);
    else if (value !== false && value !== undefined && value !== null) node.setAttribute(name, value === true ? "" : value);
  }
  node.append(...children.filter(child => child !== null && child !== undefined));
  return node;
}

export const money = amount => Number(amount).toFixed(2);
// The last 8 characters: our IDs are version 7 GUIDs, which start with a timestamp, so their first 8 characters are
// the same for everything created within about a minute
export const shortId = id => String(id).slice(-8);
export const when = iso => new Date(iso).toLocaleString();
export const clock = () => new Date().toLocaleTimeString();

export function statusPill(status) {
  return el("span", { class: "pill", "data-status": status }, status);
}

export function showConnection(pill, state) {
  const labels = { live: "Live", reconnecting: "Reconnecting…", offline: "Offline" };
  pill.dataset.state = state;
  pill.textContent = labels[state];
}

// The live events log, newest first, so an interview can see what arrived and what was ignored
export function logEvent(list, text, ignored = false) {
  list.prepend(el("li", { class: ignored ? "ignored" : null }, `${clock()}  ${text}`));
  while (list.children.length > 30) list.lastElementChild.remove();
}
