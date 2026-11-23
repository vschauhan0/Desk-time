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
import type { Role } from "@/lib/types";

export interface NavItem {
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
      { href: "/", label: "Overview", icon: LayoutDashboard },
      { href: "/live", label: "Live Monitoring", icon: Activity },
    ],
  },
  {
    title: "People & devices",
    items: [
      { href: "/employees", label: "Employees", icon: Users },
      { href: "/devices", label: "Devices", icon: Monitor },
      { href: "/device-health", label: "Device Health", icon: HeartPulse },
    ],
  },
  {
    title: "Activity",
    items: [
      { href: "/applications", label: "Application Activity", icon: AppWindow },
      { href: "/processes", label: "Process Timeline", icon: Cpu },
      { href: "/timeline", label: "Employee Timeline", icon: FolderClock },
      { href: "/active-idle", label: "Active / Idle", icon: Timer },
    ],
  },
  {
    title: "Events",
    items: [
      { href: "/usb", label: "USB Events", icon: Usb },
      { href: "/files", label: "File Activity", icon: FileText },
      { href: "/security", label: "Security & Agent Health", icon: ShieldAlert },
    ],
  },
  {
    title: "Administration",
    items: [
      { href: "/reports", label: "Reports", icon: BarChart3 },
      { href: "/settings", label: "Settings", icon: Settings, minRole: "ADMIN" },
      { href: "/audit", label: "Audit Log", icon: ScrollText, minRole: "ADMIN" },
    ],
  },
];

/** True when `pathname` belongs to nav item `href` ("/" only matches exactly). */
export function isActivePath(pathname: string, href: string): boolean {
  if (href === "/") return pathname === "/";
  return pathname === href || pathname.startsWith(`${href}/`);
}
