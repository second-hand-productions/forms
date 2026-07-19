# Deploy

The app is built and run as a Docker container by the self-hosted GitHub
Actions runner on the Ubuntu box (the runner and the deploy target are the same
machine, so there is no SSH step). nginx fronts it at `http://forms.lan/`.

The build/run/health-check steps live directly in
[`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml); use the
workflow's **Run workflow** button to deploy a chosen branch, tag or SHA by hand.

- [`forms.nginx.conf`](forms.nginx.conf) — the vhost, proxying `forms.lan` to
  the container on `127.0.0.1:8080`.
- [`deploy-forms-nginx.sh`](deploy-forms-nginx.sh) — privileged half: installs
  the vhost and reloads nginx.

## One-time server setup

The privileged script is deliberately **not** installed by the workflow — that
would let anyone who can push to this repo run arbitrary root commands. Copy it
into place by hand instead (and again whenever its contents change):

```bash
sudo install -o root -g root -m 0755 \
  deploy/deploy-forms-nginx.sh /usr/local/sbin/deploy-forms-nginx.sh
```

Grant the runner permission to run exactly that one script as root:

```bash
echo 'mushbrain ALL=(ALL) NOPASSWD: /usr/local/sbin/deploy-forms-nginx.sh' \
  | sudo tee /etc/sudoers.d/forms-deploy
sudo chmod 0440 /etc/sudoers.d/forms-deploy
sudo visudo -c          # validate before trusting it
```

Until this is done the workflow's vhost step skips with a notice; the container
deploy itself still works.

## DNS

`forms.lan` must resolve to the Ubuntu box (`192.168.0.183`). The `.lan` names
are not wildcarded — unregistered names fall through to the router — so add a
static DNS entry on the router for `forms.lan`.

## Closing the raw port

While nginx is not yet in place the container binds `0.0.0.0:8080`, so the app
is reachable at `http://ubuntu.lan:8080`. Once `forms.lan` works, set
`BIND_ADDR: "127.0.0.1"` in the workflow's `env:` block so only nginx can reach
the container.
