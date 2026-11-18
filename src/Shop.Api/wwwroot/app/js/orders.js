// The customer's order page (task R4-08): their orders, and each status change as it happens. Live updates come from
// /hubs/orders, which sends each customer only their own orders' events (Clients.User on the server).
import {
  api, clearToken, claims, connectLive, describe, el, isNewer, isUsable, logEvent, mergeSnapshot, money, OrderStatusChanged,
  readToken, register, ResyncRequired, shortId, showConnection, signIn, statusPill, when,
} from "./session.js";

const page = {
  live: document.getElementById("live"),
  who: document.getElementById("who"),
  signOut: document.getElementById("sign-out"),
  signIn: document.getElementById("sign-in"),
  signInForm: document.getElementById("sign-in-form"),
  register: document.getElementById("register"),
  signInError: document.getElementById("sign-in-error"),
  account: document.getElementById("account"),
  orderForm: document.getElementById("order-form"),
  product: document.getElementById("product"),
  quantity: document.getElementById("quantity"),
  orderError: document.getElementById("order-error"),
  notice: document.getElementById("notice"),
  orders: document.getElementById("orders"),
  noOrders: document.getElementById("no-orders"),
  details: document.getElementById("details"),
  detailsTitle: document.getElementById("details-title"),
  detailsStatus: document.getElementById("details-status"),
  detailsLines: document.getElementById("details-lines"),
  detailsHistory: document.getElementById("details-history"),
  events: document.getElementById("events"),
};

// Order ID to the row it shows: id, status, total, itemCount, createdAt, version
const orders = new Map();
const freshIds = new Set();
let selectedId = null;
let token = null;
let connection = null;

function render() {
  const rows = [...orders.values()].sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
  page.orders.replaceChildren(...rows.map(order => el("tr", {
    class: freshIds.has(order.id) ? "fresh" : null,
    "data-selectable": true,
    "aria-selected": String(order.id === selectedId),
    tabindex: "0",
    onclick: () => report(showDetails(order.id)),
    onkeydown: event => { if (event.key === "Enter") report(showDetails(order.id)); },
  },
    el("td", {}, when(order.createdAt)),
    el("td", { title: order.id }, shortId(order.id)),
    el("td", { class: "amount" }, String(order.itemCount)),
    el("td", { class: "amount" }, money(order.total)),
    el("td", {}, statusPill(order.status)))));
  freshIds.clear();
  page.noOrders.hidden = rows.length > 0;
}

async function loadOrders() {
  const result = await api("/api/v1/orders?pageSize=50", { token });
  mergeSnapshot(orders, result.items);
  render();
  if (selectedId) await showDetails(selectedId);
}

async function loadProducts() {
  const result = await api("/api/v1/products?pageSize=50", { token });
  page.product.replaceChildren(...result.items.map(product =>
    el("option", { value: product.id }, `${product.name} (${money(product.price)})`)));
}

async function showDetails(orderId) {
  selectedId = orderId;
  const order = await api(`/api/v1/orders/${orderId}`, { token });

  // The detail answer can be newer than the list row, and the list is what live events compare against; or older, when
  // a read replica is a moment behind an event already shown, and then the row's status stays
  const row = orders.get(order.id);
  if (row && order.version > row.version) orders.set(order.id, { ...row, status: order.status, version: order.version });
  const status = row && row.version > order.version ? row.status : order.status;

  page.details.hidden = false;
  page.detailsTitle.textContent = `Order ${shortId(order.id)}`;
  page.detailsStatus.replaceChildren("Status: ", statusPill(status), ` (placed ${when(order.createdAt)}, total ${money(order.total)})`);
  page.detailsLines.replaceChildren(...order.lines.map(line => el("tr", {},
    el("td", {}, line.productName),
    el("td", { class: "amount" }, money(line.unitPrice)),
    el("td", { class: "amount" }, String(line.quantity)),
    el("td", { class: "amount" }, money(line.lineTotal)))));
  page.detailsHistory.replaceChildren(...order.history.map(change =>
    el("li", {}, `${when(change.changedAt)}  ${change.fromStatus ? `${change.fromStatus} → ` : "Placed as "}${change.toStatus}`)));
  render();
}

// Shows what went wrong in a click or a live update, rather than leaving it in the console
function report(work) {
  work.catch(error => {
    if (error.status === 401) return signOut("Your session has ended. Sign in again.");
    page.notice.textContent = describe(error);
  });
}

function onOrderStatusChanged(event) {
  report(applyStatusChange(event));
}

// The server's live subscription (re)started, and events from before it are lost
function onResync() {
  logEvent(page.events, `${ResyncRequired}: reloading your orders`);
  report(loadOrders());
}

async function applyStatusChange(event) {
  const shown = orders.get(event.orderId);
  const newer = isNewer(event, shown);
  logEvent(page.events, `${OrderStatusChanged} ${shortId(event.orderId)} ${event.fromStatus} → ${event.status} v${event.version}`
    + (newer ? "" : " (ignored: not newer than what is shown)"), !newer);
  if (!newer) return;

  if (shown) {
    orders.set(event.orderId, { ...shown, status: event.status, version: event.version });
    freshIds.add(event.orderId);
    render();
  } else {
    // An order this page hasn't listed, such as one placed in another tab
    await loadOrders();
  }

  if (event.orderId === selectedId) await showDetails(selectedId);
}

function onConnection(state, error) {
  showConnection(page.live, state);
  if (state === "offline" && token && !isUsable(token, "Customer")) {
    signOut("Your session has ended. Sign in again.");
  } else if (state === "offline" && error) {
    page.notice.textContent = "Live updates stopped. Reload the page to reconnect.";
  }
}

async function start() {
  page.signIn.hidden = true;
  page.account.hidden = false;
  page.signOut.hidden = false;
  page.who.textContent = claims(token).email;
  try {
    // Connected first and loaded second, so a status change in between arrives as an event and nothing is missed
    connection = await connectLive({
      hub: "/hubs/orders",
      token,
      handlers: { [OrderStatusChanged]: onOrderStatusChanged, [ResyncRequired]: onResync },
      onState: onConnection,
      onReconnected: loadOrders,
    });
    await Promise.all([loadOrders(), loadProducts()]);
  } catch (error) {
    if ((error.status ?? error.statusCode) === 401) return signOut("Your session has ended. Sign in again.");
    page.notice.textContent = `Can't load your orders: ${describe(error)}`;
  }
}

async function signOut(message = "") {
  token = null;
  clearToken();
  orders.clear();
  selectedId = null;
  await connection?.stop();
  connection = null;
  showConnection(page.live, "offline");
  page.who.textContent = "";
  page.signOut.hidden = true;
  page.account.hidden = true;
  page.details.hidden = true;
  page.signIn.hidden = false;
  page.signInError.textContent = message;
}

async function enter(signInWith) {
  page.signInError.textContent = "";
  try {
    const signedIn = await signInWith(page.signInForm.email.value, page.signInForm.password.value);
    if (!isUsable(signedIn, "Customer")) return signOut("This page is for customers. Admins use the dashboard at /app/admin.html.");
    token = signedIn;
    page.signInForm.reset();
    await start();
  } catch (error) {
    page.signInError.textContent = describe(error);
  }
}

page.signInForm.addEventListener("submit", event => {
  event.preventDefault();
  enter(signIn);
});

page.register.addEventListener("click", () => {
  if (page.signInForm.reportValidity()) enter(register);
});

page.orderForm.addEventListener("submit", async event => {
  event.preventDefault();
  page.orderError.textContent = "";
  const button = page.orderForm.querySelector("button");
  button.disabled = true;
  try {
    const order = await api("/api/v1/orders", {
      method: "POST",
      token,
      body: { lines: [{ productId: page.product.value, quantity: Number(page.quantity.value) }] },
    });
    orders.set(order.id, { ...order, itemCount: order.lines.reduce((sum, line) => sum + line.quantity, 0) });
    freshIds.add(order.id);
    await showDetails(order.id);
  } catch (error) {
    if (error.status === 401) return signOut("Your session has ended. Sign in again.");
    page.orderError.textContent = describe(error);
  } finally {
    button.disabled = false;
  }
});

page.signOut.addEventListener("click", () => signOut());

token = readToken();
if (isUsable(token, "Customer")) {
  start();
} else {
  signOut();
}
