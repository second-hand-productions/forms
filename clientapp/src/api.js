// The app is served under a base path (/forms/ in production), so API calls have
// to be resolved against it. A bare '/api/...' would escape to the site root and
// hit the shared nginx root instead of this app — a 404.
//
// import.meta.env.BASE_URL comes from `base` in vite.config.js and always has a
// trailing slash.
const API_ROOT = `${import.meta.env.BASE_URL}api`

/** Build an absolute URL for an API route, e.g. apiUrl('/forms') -> /forms/api/forms */
export function apiUrl(path) {
  return `${API_ROOT}${path}`
}
