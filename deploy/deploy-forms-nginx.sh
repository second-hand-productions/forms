#!/usr/bin/env bash
#
# Privileged half of the forms deploy. Installed to /usr/local/sbin/ as
# root-owned 0755 and run by the GitHub Actions runner via a single NOPASSWD
# sudoers rule:
#
#   mushbrain ALL=(ALL) NOPASSWD: /usr/local/sbin/deploy-forms-nginx.sh
#
# The runner can therefore perform exactly this deploy and nothing else with
# root. This file in the repo is the source of truth for the script's contents,
# but it is NOT auto-installed by the workflow (that would defeat the trust
# boundary) -- copy it into place manually whenever it changes. See
# deploy/README.md.
#
# The arg is treated strictly as a file path (quoted, no eval), so a hostile
# value can at worst fail or install a bad nginx config -- it cannot run
# arbitrary root commands.
set -euo pipefail

NGINX_CONF="${1:?usage: deploy-forms-nginx.sh <nginx_conf>}"

# Only this app's fragment is touched; the shared root vhost is infrastructure
# and is never rewritten by an app pipeline.
install -d -o root -g root -m 0755 /etc/nginx/apps.d
install -o root -g root -m 0644 "$NGINX_CONF" /etc/nginx/apps.d/forms.conf

# Retire the standalone vhost from the pre-path-based layout, if present.
rm -f /etc/nginx/sites-enabled/forms

# Validate before reloading. On failure, set -e aborts here and the running
# nginx keeps its current in-memory config untouched.
nginx -t
systemctl reload nginx
