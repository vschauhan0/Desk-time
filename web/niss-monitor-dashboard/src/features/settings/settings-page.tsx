"use client";

import { useState } from "react";
import { Notice, PageHeader } from "@/components/ui/primitives";
import { Tabs, type TabDef } from "@/components/ui/widgets";
import { useAuth, usePageAccess } from "@/providers/auth-provider";
import { AdminUsersPanel } from "./admin-users-panel";
import { CategoriesEditor } from "./categories-editor";
import { PoliciesEditor } from "./policies-editor";
import { RetentionPanel, ScreenshotStoragePanel, ThresholdsPanel } from "./simple-settings";
import { TokensPanel } from "./tokens-panel";

type TabKey = "policies" | "categories" | "retention" | "thresholds" | "tokens" | "admins";

export function SettingsPage() {
  const { isAdmin, isSuperAdmin } = useAuth();
  const access = usePageAccess();
  const [tab, setTab] = useState<TabKey>("policies");

  if (!isAdmin) {
    return (
      <>
        <PageHeader title="Settings" />
        <Notice tone="warn">Settings are available to administrators only.</Notice>
      </>
    );
  }

  // Each settings tab maps to a settings.<key> sub-page; hide those the user cannot view.
  const allTabs: { key: TabKey; label: string; page: string }[] = [
    { key: "policies", label: "Agent policies", page: "settings.policies" },
    { key: "categories", label: "Application categories", page: "settings.categories" },
    { key: "retention", label: "Retention", page: "settings.retention" },
    { key: "thresholds", label: "Health thresholds", page: "settings.thresholds" },
    { key: "tokens", label: "Enrollment tokens", page: "settings.tokens" },
    ...(isSuperAdmin ? [{ key: "admins" as const, label: "Administrators", page: "settings.admins" }] : []),
  ];
  const tabs: TabDef<TabKey>[] = allTabs.filter((t) => access.canView(t.page)).map(({ key, label }) => ({ key, label }));
  const activeTab: TabKey = tabs.some((t) => t.key === tab) ? tab : (tabs[0]?.key ?? "policies");

  return (
    <>
      <PageHeader title="Settings" subtitle="Monitoring policy, categorisation, retention and access. Every change is recorded in the audit log." />
      <Tabs tabs={tabs} value={activeTab} onChange={setTab} />
      <div role="tabpanel">
        {activeTab === "policies" && <PoliciesEditor />}
        {activeTab === "categories" && <CategoriesEditor />}
        {activeTab === "retention" && <div className="max-w-3xl space-y-5"><ScreenshotStoragePanel /><RetentionPanel /></div>}
        {activeTab === "thresholds" && <div className="max-w-3xl"><ThresholdsPanel /></div>}
        {activeTab === "tokens" && <TokensPanel />}
        {activeTab === "admins" && isSuperAdmin && <AdminUsersPanel />}
      </div>
    </>
  );
}
