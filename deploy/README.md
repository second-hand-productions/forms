# Deploy

The app is built and run as a Docker container by the self-hosted GitHub Actions
runner on the Ubuntu box (the runner and the deploy target are the same machine,
so there is no SSH step). It is served at **`/forms/`** on the shared nginx
root — the same path on both networks:

- LAN — `http://ubuntu.lan/forms/`
- Tailscale — `https://ubuntuserver.tailb99a87.ts.net/forms/`

The build/run/health-check steps live directly in
[`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml); use the
workflow's **Run workflow** button to deploy a chosen branch, tag or SHA by hand.

## Layout

The **root vhost is infrastructure**, owned by no application. It holds
`server_name`, the landing page and the shared backends (`/pb/`, `/mqtt`), and
includes `/etc/nginx/apps.d/*.conf`. Each app pipeline installs only its own
fragment there:

```
/etc/nginx/sites-available/root   <- deploy/root.nginx.conf   (installed by hand)
/etc/nginx/apps.d/forms.conf      <- deploy/forms.nginx.conf  (this pipeline)
/etc/nginx/apps.d/foodbowl.conf   <- FlutterFoodBowl pipeline
```

Adding an app means dropping one more fragment in `apps.d/`. No new DNS name, no
new Tailscale port, no edits to another project's config.

The `/forms` prefix is **forwarded, not stripped**: nginx passes it through and
ASP.NET removes it via `app.UsePathBase`. That value must stay in sync with
`base` in `clientapp/vite.config.js`, which is baked into asset URLs at build
time.

## One-time server setup

Copy the privileged script and the root vhost across:

```bash
ssh ubuntu 'mkdir -p ~/deploy'
scp deploy/deploy-forms-nginx.sh deploy/root.nginx.conf ubuntu:~/deploy/
```

Install the script root-owned. The runner may run it but not modify it — that is
the trust boundary, which is why the workflow does not install it:

```bash
sudo install -o root -g root -m 0755 \
  ~/deploy/deploy-forms-nginx.sh /usr/local/sbin/deploy-forms-nginx.sh

echo 'mushbrain ALL=(ALL) NOPASSWD: /usr/local/sbin/deploy-forms-nginx.sh' \
  | sudo tee /etc/sudoers.d/forms-deploy
sudo chmod 0440 /etc/sudoers.d/forms-deploy
sudo visudo -c
```

Reinstall it whenever its contents change in this repo.

## Migrating to the shared root (one-off cutover)

Do this in a single step so no hostname is ever left unclaimed. `nginx -t` gates
the reload, so a mistake leaves the running config untouched:

```bash
sudo install -o root -g root -m 0644 ~/deploy/root.nginx.conf \
  /etc/nginx/sites-available/root
sudo ln -sf /etc/nginx/sites-available/root /etc/nginx/sites-enabled/root

# Retire the stock default and the app-owned shared vhost.
sudo rm -f /etc/nginx/sites-enabled/default /etc/nginx/sites-enabled/foodbowl \
           /etc/nginx/sites-enabled/forms

# Seed both fragments so nothing 404s between now and the next deploys.
sudo install -d -o root -g root -m 0755 /etc/nginx/apps.d
sudo install -o root -g root -m 0644 ~/deploy/forms.nginx.conf \
  /etc/nginx/apps.d/forms.conf

sudo nginx -t && sudo systemctl reload nginx
```

Then collapse Tailscale back to a single mapping — `/forms/` is reachable
through the root, so the per-app port is no longer needed:

```bash
sudo tailscale serve --https=8443 off
tailscale serve status
```

## Closing the raw port

Once `/forms/` works through nginx, set `BIND_ADDR: "127.0.0.1"` in the
workflow's `env:` block. Both the LAN path (nginx) and the Tailscale path
(`serve` -> nginx) reach the container over loopback, so only the direct
`ubuntu.lan:8080` bypass disappears.
