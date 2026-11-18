// The admin dashboard (task R4-08): every order, with new ones appearing as customers place them (OrderPlaced from
// /hubs/admin, which refuses anyone without the Admin role). Status changes go to customers, not admins (R4-07), so a
// status shown here changes when this admin moves the order, and on every reload.
import {
  api, clearToken, claims, connectLive, describe, el, isNewer, isUsable, logEvent, mergeSnapshot, money, OrderPlaced,
  readToken, ResyncRequired, shortId, showConnection, signIn, statusPill, when,
} from "./session.js";

const nextStatuses = {
  Pending: ["Confirmed", "Cancelled"],
  Confirmed: ["Shipped", "Cancelled"],
  Shipped: ["Delivered"],
  Delivered: [],
  Cancelled: [],
};

const page = {
  live: document.getElementById("live"),
  who: document.getElementById("who"),
  signOut: document.getElementById("sign-out"),
  signIn: document.getElementById("sign-in"),
  signInForm: document.getElementById("sign-in-form"),
  signInError: document.getElementById("sign-in-error"),
  dashboard: document.getElementById("dashboard"),
  notice: document.getElementById("notice"),
  orders: document.getElementById("orders"),
  noOrders: document.getElementById("no-orders"),
  events: document.getElementById("events"),
};

// Order ID to the row it shows: id, customerId, status, total, itemCount, createdAt, version
const orders = new Map();
const freshIds = new Set();
let token = null;
let connection = null;

function render() {
  const rows = [...orders.values()].sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
  page.orders.replaceChildren(...rows.map(order => el("tr", { class: freshIds.has(order.id) ? "fresh" : null },
    el("td", {}, when(order.createdAt)),
    el("td", { title: order.id }, shortId(order.id)),
    el("td", { title: order.customerId }, shortId(order.customerId)),
    el("td", { class: "amount" }, String(order.itemCount)),
    el("td", { class: "amount" }, money(order.total)),
    el("td", {}, statusPill(order.status)),
    el("td", {}, el("div", { class: "actions" }, ...nextStatuses[order.status].map(status =>
      el("button", { type: "button", onclick: event => moveTo(order, status, event.currentTarget) }, status)))))));
  freshIds.clear();
  page.noOrders.hidden = rows.length > 0;
}

async function load() {
  const result = await api("/api/v1/orders?pageSize=50", { token });
  mergeSnapshot(orders, result.items);
  render();
}

// Shows what went wrong in a live update or a reload, rather than leaving it in the console
function report(work) {
  work.catch(error => {
    if (error.status === 401) return signOut("Your session has ended. Sign in again.");
    page.notice.textContent = describe(error);
  });
}

async function moveTo(order, status, button) {
  button.disabled = true;
  page.notice.textContent = "";
  try {
    const updated = await api(`/api/v1/orders/${order.id}/status`, { method: "PATCH", body: { status }, token });
    // The answer is newer than anything on screen; its own live event then arrives with the same version and is ignored
    orders.set(order.id, { ...order, status: updated.status, version: updated.version, updatedAt: updated.updatedAt });
    render();
  } catch (error) {
    if (error.status === 401) return signOut("Your session has ended. Sign in again.");
    // 409: another admin changed it first, or the move isn't allowed from its current status
    page.notice.textContent = `Order ${shortId(order.id)}: ${describe(error)}`;
    await load();
  }
}

function onOrderPlaced(event) {
  // A row already loaded from the API, or the same event delivered twice, carries the same version and changes nothing
  const shown = orders.get(event.orderId);
  const newer = isNewer(event, shown);
  logEvent(page.events, `${OrderPlaced} ${shortId(event.orderId)} ${money(event.total)} v${event.version}`
    + (newer ? "" : " (ignored: already shown)"), !newer);
  if (!newer) return;

  orders.set(event.orderId, {
    ...shown,
    id: event.orderId,
    customerId: event.customerId,
    status: event.status,
    total: event.total,
    itemCount: event.itemCount,
    createdAt: event.placedAt,
    version: event.version,
  });
  freshIds.add(event.orderId);
  render();
}

// The server's live subscription (re)started, and events from before it are lost
function onResync() {
  logEvent(page.events, `${ResyncRequired}: reloading the list`);
  report(load());
}

function onConnection(state, error) {
  showConnection(page.live, state);
  if (state === "offline" && token && !isUsable(token, "Admin")) {
    signOut("Your session has ended. Sign in again.");
  } else if (state === "offline" && error) {
    page.notice.textContent = "Live updates stopped. Reload the page to reconnect.";
  }
}

async function start() {
  page.signIn.hidden = true;
  page.dashboard.hidden = false;
  page.signOut.hidden = false;
  page.who.textContent = claims(token).email;
  try {
    // Connected first and loaded second, so an order placed in between arrives as an event and nothing is missed
    connection = await connectLive({
      hub: "/hubs/admin",
      token,
      handlers: { [OrderPlaced]: onOrderPlaced, [ResyncRequired]: onResync },
      onState: onConnection,
      onReconnected: load,
    });
    await load();
  } catch (error) {
    // ApiError carries status; SignalR's negotiation error carries statusCode
    if ([401, 403].includes(error.status ?? error.statusCode) || /\b40[13]\b/.test(error.message)) {
      return signOut("This page is for admins. Sign in with an admin account.");
    }
    page.notice.textContent = `Can't load the dashboard: ${describe(error)}`;
  }
}

async function signOut(message = "") {
  token = null;
  clearToken();
  orders.clear();
  await connection?.stop();
  connection = null;
  showConnection(page.live, "offline");
  page.who.textContent = "";
  page.signOut.hidden = true;
  page.dashboard.hidden = true;
  page.signIn.hidden = false;
  page.signInError.textContent = message;
}

page.signInForm.addEventListener("submit", async event => {
  event.preventDefault();
  page.signInError.textContent = "";
  try {
    const signedIn = await signIn(page.signInForm.email.value, page.signInForm.password.value);
    if (!isUsable(signedIn, "Admin")) return signOut("This page is for admins. Sign in with an admin account.");
    token = signedIn;
    page.signInForm.reset();
    await start();
  } catch (error) {
    page.signInError.textContent = describe(error);
  }
});

page.signOut.addEventListener("click", () => signOut());

token = readToken();
if (isUsable(token, "Admin")) {
  start();
} else {
  signOut();
}
