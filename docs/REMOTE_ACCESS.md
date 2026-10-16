# Checking your agents from a phone (no cloud relay)

Overhive has a compact page for the attention queue at **`/m`** (for example
`http://127.0.0.1:47821/m`): what needs you, with Open, Message, Snooze and Dismiss. The daemon always
listens on `127.0.0.1` only. To use it from a phone, bring the phone to the daemon through a channel you own.
Overhive never uses a vendor relay.

**Read this first.** The dashboard page embeds the API token. Whoever can open the page can do everything
you can in the dashboard: read agent output and diffs, message agents, pause or stop them. Only expose it to
your own devices.

## Option 1: SSH tunnel (nothing to configure in Overhive)

From an SSH client on the phone (for example Termius or Blink), forward a local port to the daemon:

```
ssh -L 47821:127.0.0.1:47821 you@your-computer
```

Then open `http://localhost:47821/m` on the phone. Use the **same port number** on both ends: the Origin
check accepts loopback origins on the daemon's own port only.

## Option 2: Tailscale `tailscale serve` (your tailnet only)

On the computer running Overhive:

```
tailscale serve --bg 47821
```

This publishes `https://<machine>.<tailnet>.ts.net/` to devices in your tailnet only (HTTPS by Tailscale).
Never use `tailscale funnel`, which makes it public. Restrict access with Tailscale ACLs if others share
your tailnet.

`tailscale serve` forwards the original Host name, which Overhive rejects by default (`421`, the
DNS-rebinding defence). Allow that one exact name in `~/.config/agent-mission-control/config.json` and
restart the daemon:

```json
{ "AllowedHosts": ["my-laptop.tail1234.ts.net"] }
```

* `AllowedHosts` is empty by default. Entries are exact host names or IP addresses; wildcards (`*`), ports
  and URLs are refused at startup.
* A request is accepted when its Host is loopback or listed. A browser Origin from a listed name is only
  accepted for requests addressed to that same name.
* The token is still required for every API call. The daemon still binds `127.0.0.1` only; Tailscale
  connects to it locally.
* The daemon logs a warning at startup while `AllowedHosts` is set.

Then open `https://my-laptop.tail1234.ts.net/m` on the phone.

## Notifications on the phone

Phone browsers often allow web notifications only for installed web apps, and only while the page runs. For
reliable phone alerts, use your own ntfy server (`Notifications.NtfyUrl`, see [NOTIFICATIONS.md](NOTIFICATIONS.md)),
which can also be reached over Tailscale.
