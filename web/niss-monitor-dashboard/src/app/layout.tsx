import type { Metadata, Viewport } from "next";
import type { ReactNode } from "react";
import { AuthProvider } from "@/providers/auth-provider";
import "./globals.css";

export const metadata: Metadata = {
  title: { default: "NISS PC Monitor", template: "%s - NISS PC Monitor" },
  description: "NISS Employee PC Monitoring System - administrator dashboard",
  robots: { index: false, follow: false },
};

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#f4f6f9" },
    { media: "(prefers-color-scheme: dark)", color: "#0c0f15" },
  ],
};

/** Runs before first paint so the chosen theme never flashes. */
const THEME_SCRIPT = `(function(){try{var t=localStorage.getItem('niss.theme');if(t!=='light'&&t!=='dark'){t=window.matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light';}document.documentElement.setAttribute('data-theme',t);}catch(e){}})();`;

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: THEME_SCRIPT }} />
      </head>
      <body>
        <AuthProvider>{children}</AuthProvider>
      </body>
    </html>
  );
}
