#!/usr/bin/env bash
# Interview steps 5 and 6 (task R4-10): the live pages. It creates two customers, Alice and Bob, prints what to do in
# which browser tab, and checks without a browser that the admin hub refuses a customer's token, both when the
# client negotiates and when it opens the WebSocket straight away with the token in the URL, as browsers send it.
#
#   tests/proof/requirement-4/4-live-pages.sh
source "$(dirname "$0")/lib.sh"

stamp=$(date +%s)
alice="alice-$stamp@shop.test"
bob="bob-$stamp@shop.test"
alice_token=$(new_customer "$alice")
register "$bob"
admin_email=$(setting ADMIN_EMAIL admin@shop.local)
admin_token=$(sign_in "$admin_email" "$(setting ADMIN_PASSWORD 'Admin123!')")

step "5. Admin dashboard: a new order appears at once"
say "Tab 1: open $API/app/admin.html and sign in as the admin ($admin_email)."
say "Tab 2: open $API/app and sign in as $alice, password $PASSWORD."
say "In tab 2, place an order. Tab 1 shows it at once, without a reload, and its Live events box logs OrderPlaced."

step "6. Customer page: the admin's change reaches its customer, and only them"
say "Tab 3: open $API/app and sign in as $bob, same password."
say "In tab 1, click Confirmed on Alice's order. Tab 2 shows Confirmed at once and logs OrderStatusChanged;"
say "tab 3's Live events box stays empty."

step "6b. The admin hub refuses a customer (checked without a browser)"
negotiate() {
  curl -sS -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer $2" "$API/hubs/$1/negotiate?negotiateVersion=1"
}
# Opens the WebSocket without negotiating, the token in the query string. 101 means it opened; curl then stops
websocket() {
  curl -sS -o /dev/null -w '%{http_code}' --http1.1 --max-time 2 -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
    -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: cHJvb2Ytb2YtY29uY2VwdA==' \
    "$API/hubs/$1?access_token=$2" 2>/dev/null || true
}
check() {
  local what=$1 expected=$2 actual=$3
  [[ $actual == "$expected" ]] || fail "$what: expected $expected, got $actual"
  say "$what: $actual"
}
check "Alice's token, negotiate with /hubs/admin" 403 "$(negotiate admin "$alice_token")"
check "Alice's token in the URL, WebSocket to /hubs/admin" 403 "$(websocket admin "$alice_token")"
check "the admin's token, negotiate with /hubs/admin" 200 "$(negotiate admin "$admin_token")"
check "the admin's token in the URL, WebSocket to /hubs/admin" 101 "$(websocket admin "$admin_token")"
check "Alice's token, negotiate with /hubs/orders" 200 "$(negotiate orders "$alice_token")"
