"use client";

import { ArrowDown, ArrowUp, Plus, Save, Trash2 } from "lucide-react";
import { useState } from "react";
import { Button, Card, Notice } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { api } from "@/lib/api";
import { APP_CATEGORIES, type AppCategoryRule } from "@/lib/types";

/** Guid.Empty: the server assigns real ids to rows it has not seen (assumption, see README). */
const NEW_ID = "00000000-0000-0000-0000-000000000000";

function Editor({ initial, onSaved }: { initial: AppCategoryRule[]; onSaved: () => void }) {
  const [rules, setRules] = useState<AppCategoryRule[]>(() => [...initial].sort((a, b) => a.priority - b.priority));
  const [saved, setSaved] = useState(false);
  const save = useAction(async (list: AppCategoryRule[]) => api.saveAppCategories(list.map((r, i) => ({ ...r, priority: (i + 1) * 10 }))));

  const update = (i: number, patch: Partial<AppCategoryRule>) => {
    setSaved(false);
    setRules((rs) => rs.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));
  };
  const move = (i: number, dir: -1 | 1) => {
    setSaved(false);
    setRules((rs) => {
      const j = i + dir;
      if (j < 0 || j >= rs.length) return rs;
      const copy = [...rs];
      [copy[i], copy[j]] = [copy[j], copy[i]];
      return copy;
    });
  };
  const invalid = rules.some((r) => !r.pattern.trim());

  return (
    <Card
      title="Application category rules"
      actions={
        <>
          <Button size="sm" icon={Plus} onClick={() => { setSaved(false); setRules((rs) => [...rs, { id: NEW_ID, pattern: "", category: "Unknown", priority: (rs.length + 1) * 10 }]); }}>Add rule</Button>
          <Button size="sm" variant="primary" icon={Save} loading={save.pending} disabled={invalid} onClick={() => void save.run(rules).then((r) => { if (r) { setSaved(true); onSaved(); } })}>Save all</Button>
        </>
      }
      padded={false}
    >
      <div className="space-y-3 p-4">
        <p className="text-[13px] text-muted">Rules are evaluated top to bottom; the first pattern that matches an executable name wins. Use <span className="mono">*</span> as a wildcard (for example <span className="mono">chrome*.exe</span>). Saving replaces the whole list.</p>
        {save.error && <Notice tone="danger">{save.error}</Notice>}
        {saved && <Notice tone="ok">Rules saved.</Notice>}
      </div>
      <div className="table-wrap !rounded-none !border-x-0 !border-b-0">
        <table className="data-table">
          <thead><tr><th className="w-12">#</th><th>Pattern</th><th className="w-52">Category</th><th className="w-32" /></tr></thead>
          <tbody>
            {rules.map((r, i) => (
              <tr key={`${r.id}-${i}`}>
                <td className="text-faint">{i + 1}</td>
                <td><input className="field mono" value={r.pattern} onChange={(e) => update(i, { pattern: e.target.value })} aria-label={`Pattern ${i + 1}`} placeholder="code.exe" /></td>
                <td>
                  <select className="field" value={r.category} onChange={(e) => update(i, { category: e.target.value })} aria-label={`Category ${i + 1}`}>
                    {APP_CATEGORIES.map((c) => <option key={c} value={c}>{c}</option>)}
                  </select>
                </td>
                <td>
                  <div className="flex justify-end gap-1">
                    <Button size="sm" variant="ghost" iconOnly icon={ArrowUp} aria-label="Move up" disabled={i === 0} onClick={() => move(i, -1)} />
                    <Button size="sm" variant="ghost" iconOnly icon={ArrowDown} aria-label="Move down" disabled={i === rules.length - 1} onClick={() => move(i, 1)} />
                    <Button size="sm" variant="ghost" iconOnly icon={Trash2} aria-label="Delete rule" onClick={() => { setSaved(false); setRules((rs) => rs.filter((_, idx) => idx !== i)); }} />
                  </div>
                </td>
              </tr>
            ))}
            {rules.length === 0 && <tr><td colSpan={4} className="py-6 text-center text-muted">No rules. Applications are categorised as Unknown.</td></tr>}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

export function CategoriesEditor() {
  const { data, error, loading, mutate } = useApi<AppCategoryRule[]>("/settings/app-categories");
  return (
    <DataState loading={loading} error={error} onRetry={() => void mutate()}>
      {data && <Editor key={data.map((r) => r.id).join(",")} initial={data} onSaved={() => void mutate()} />}
    </DataState>
  );
}
