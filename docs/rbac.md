# Access control (RBAC)

Two layers of access, set per dashboard user under **Settings → Administrators** (SUPERADMIN only):

1. **Role** — the coarse capability level: `VIEWER` (read-only) < `ADMIN` (device actions, settings) < `SUPERADMIN` (also manages administrators and this access control).
2. **Per-user limits** — on top of the role, an ADMIN or VIEWER can be restricted to **specific pages** and **specific employees**. A SUPERADMIN is always unrestricted; the limits below do not apply to them.

## Page access

Each user may be allowed **all pages their role permits** (the default) or **only the pages you choose**. A withheld page is:

- hidden from the sidebar,
- blocked by the client route guard (navigating to it redirects to the user's first allowed page), and
- unreachable in the sense that its data endpoints are still role-gated and employee-scoped.

Page keys are fixed (`overview`, `live`, `employees`, `devices`, `device-health`, `applications`, `processes`, `timeline`, `active-idle`, `usb`, `files`, `security`, `reports`, `settings`, `audit`). A page above the user's role can never be granted — e.g. a VIEWER cannot be given `settings`; the server drops it.

The signed-in user's allowed pages are returned by `GET /auth/me` (`allowedPages`; null = all role-allowed pages). The editor's page catalogue comes from `GET /admin-users/pages`.

## Employee access

A user may see **all employees** (default) or be limited to:

- **departments** — every current and future employee in the chosen departments, and/or
- **individual employees** — an explicit list.

The effective set is the union of both. This is enforced **server-side**, not just hidden in the UI.

### How it is enforced

Scoping resolves to a set of **employee ids** (explicit ∪ department members) and the **device ids** those employees own (`AccessScope`). Enforcement happens at two kinds of point:

- The central `IScopeResolver` — every read that filters through `Scope.InScope(...)` (applications, processes, USB/file events, timelines, reports, active/idle, employee summaries) is **capped** to the user's allowed devices/employees. A restricted user cannot widen past their allowance with query parameters.
- Bespoke endpoints that don't use `Scope` — the employee list, device list + detail, live cards, the overview landing page, and screenshot reads each apply the same `AccessScope`. An out-of-scope device or employee returns **404** (existence is not revealed), not 403.

A restricted user with an empty allowed set sees nothing (fail-closed).

> Note: stored screenshots and live view are SUPERADMIN-only regardless, so employee-scoping of screenshots only ever matters as defence-in-depth — a SUPERADMIN is never restricted.

## Data model

`admin_users` carries, in addition to `role`/`enabled`:

| Column | Meaning |
|---|---|
| `allowed_pages_json` | JSON array of page keys, or null = all role-allowed pages |
| `scope_all_employees` | true (default) = sees every employee |
| `allowed_departments_json` | JSON array of department names (when not scope-all) |
| `allowed_employee_ids_json` | JSON array of employee ids (when not scope-all) |

Existing users migrate to `scope_all_employees = true` with null page/department/employee lists — i.e. **unchanged, unrestricted** — so the feature is backward-compatible.
