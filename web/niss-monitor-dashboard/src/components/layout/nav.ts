import {
  Activity,
  AppWindow,
  BarChart3,
  Cpu,
  FileText,
  FolderClock,
  HeartPulse,
  LayoutDashboard,
  Monitor,
  ScrollText,
  Settings,
  ShieldAlert,
  Timer,
  Usb,
  Users,
  type LucideIcon,
} from "lucide-react";
import type { PageAccessMap, Role } from "@/lib/types";
import { canViewPage } from "@/lib/access";

export interface NavItem {
  /** Stable page key, matching the server's AppPages catalogue; used for per-user page access. */
  key: string;
  href: string;
  label: string;
  icon: LucideIcon;
  minRole?: Role;
}

export interface NavGroup {
  title: string;
  items: NavItem[];
}

export const NAV: NavGroup[] = [
  {
    title: "Monitor",
    items: [
      { key: "overview", href: "/", label: "Overview", icon: LayoutDashboard },
      { key: "live", href: "/live", label: "Live Monitoring", icon: Activity },
    ],
  },
  {
    title: "People & devices",
    items: [
      { key: "employees", href: "/employees", label: "Employees", icon: Users },
      { key: "devices", href: "/devices", label: "Devices", icon: Monitor },
      { key: "device-health", href: "/device-health", label: "Device Health", icon: HeartPulse },
    ],
  },
  {
    title: "Activity",
    items: [
      { key: "applications", href: "/applications", label: "Application Activity", icon: AppWindow },
      { key: "processes", href: "/processes", label: "Process Timeline", icon: Cpu },
      { key: "timeline", href: "/timeline", label: "Employee Timeline", icon: FolderClock },
      { key: "active-idle", href: "/active-idle", label: "Active / Idle", icon: Timer },
    ],
  },
  {
    title: "Events",
    items: [
      { key: "usb", href: "/usb", label: "USB Events", icon: Usb },
      { key: "files", href: "/files", label: "File Activity", icon: FileText },
      { key: "security", href: "/security", label: "Security & Agent Health", icon: ShieldAlert },
    ],
  },
  {
    title: "Administration",
    items: [
      { key: "reports", href: "/reports", label: "Reports", icon: BarChart3 },
      { key: "settings", href: "/settings", label: "Settings", icon: Settings, minRole: "ADMIN" },
      { key: "audit", href: "/audit", label: "Audit Log", icon: ScrollText, minRole: "ADMIN" },
    ],
  },
];

/** Every nav item, flattened. */
export const NAV_ITEMS: NavItem[] = NAV.flatMap((g) => g.items);

/**
 * Whether a user may open a page. `pageAccess` null = every page their role allows (the role check is separate).
 * A page not in the catalogue (e.g. a device detail route) is allowed — access to its data is enforced server-side.
 */
export function isPageAllowed(pageAccess: PageAccessMap | null | undefined, key: string | undefined): boolean {
  if (!key) return true;
  return canViewPage(pageAccess, key);
}

/** The page key owning a pathname, or undefined when the path is not a top-level page (e.g. a detail route). */
export function pageKeyForPath(pathname: string): string | undefined {
  // Longest matching route wins so "/devices/PC-1" maps to "devices".
  let best: NavItem | undefined;
  for (const item of NAV_ITEMS) {
    if (isActivePath(pathname, item.href) && (!best || item.href.length > best.href.length)) best = item;
  }
  return best?.key;
}

/** True when `pathname` belongs to nav item `href` ("/" only matches exactly). */
export function isActivePath(pathname: string, href: string): boolean {
  if (href === "/") return pathname === "/";
  return pathname === href || pathname.startsWith(`${href}/`);
}
