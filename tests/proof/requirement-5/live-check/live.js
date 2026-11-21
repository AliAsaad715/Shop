// Task R5-10's check (see live.html), with the live pages' own connectLive. A headless page can't talk to the script
// that opened it, so each result is a request to /health that nginx-checks.sh finds in Nginx's access log
import { connectLive, signIn } from "../js/session.js";

const report = (batch, i, result) =>
  fetch(`/health?batch=${batch}&i=${i}&result=${encodeURIComponent(result)}`).catch(() => {});

const credentials = new URLSearchParams(location.hash.slice(1));
const token = await signIn(credentials.get("email"), credentials.get("password"));

// Ten page loads: connect as the live pages do, then leave
for (let i = 1; i <= 10; i++) {
  try {
    const connection = await connectLive({
      hub: "/hubs/admin", token, handlers: {}, onState: () => {}, onReconnected: async () => {},
    });
    await report("connect", i, "live");
    await connection.stop();
  } catch (error) {
    await report("connect", i, `failed: ${error.message}`);
  }
}

// One connection that stays open. The script stops the instance it is on: the page has to reconnect through Nginx and
// reload what it shows, as onReconnected does on the live pages (the admin page reloads its orders)
let change = 0;
await connectLive({
  hub: "/hubs/admin",
  token,
  handlers: {},
  onState: state => report("state", ++change, state),
  onReconnected: () => fetch("/api/v1/orders?batch=reload&i=1", { headers: { Authorization: `Bearer ${token}` } }),
});
