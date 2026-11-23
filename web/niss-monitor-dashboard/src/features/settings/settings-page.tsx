"use client";

import { useState } from "react";
import { Notice, PageHeader } from "@/components/ui/primitives";
import { Tabs, type TabDef } from "@/components/ui/widgets";
import { useAuth } from "@/providers/auth-provider";
import { AdminUsersPanel } from "./admin-users-panel";
import { CategoriesEditor } from "./categories-editor";
import { PoliciesEditor } from "./policies-editor";
import { RetentionPanel, ThresholdsPanel } from "./simple-settings";
import { TokensPanel } from "./tokens-panel";

type TabKey = "policies" | "categories" | "retention" | "thresholds" | "tokens" | "admins";

export function SettingsPage() {
  const { isAdmin, isSuperAdmin } = useAuth();
  const [tab, setTab] = useState<TabKey>("policies");

  if (!isAdmin) {
    return (
      <>
        <PageHeader title="Settings" />
        <Notice tone="warn">Settings are available to administrators only.</Notice>
      </>
    );
  }

  const tabs: TabDef<TabKey>[] = [
    { key: "policies", label: "Agent policies" },
    { key: "categories", label: "Application categories" },
    { key: "retention", label: "Retention" },
    { key: "thresholds", label: "Health thresholds" },
    { key: "tokens", label: "Enrollment tokens" },
    ...(isSuperAdmin ? [{ key: "admins" as const, label: "Administrators" }] : []),
  ];

  return (
    <>
      <PageHeader title="Settings" subtitle="Monitoring policy, categorisation, retention and access. Every change is recorded in the audit log." />
      <Tabs tabs={tabs} value={tab} onChange={setTab} />
      <div role="tabpanel">
        {tab === "policies" && <PoliciesEditor />}
        {tab === "categories" && <CategoriesEditor />}
        {tab === "retention" && <div className="max-w-3xl"><RetentionPanel /></div>}
        {tab === "thresholds" && <div className="max-w-3xl"><ThresholdsPanel /></div>}
        {tab === "tokens" && <TokensPanel />}
        {tab === "admins" && isSuperAdmin && <AdminUsersPanel />}
      </div>
    </>
  );
}
