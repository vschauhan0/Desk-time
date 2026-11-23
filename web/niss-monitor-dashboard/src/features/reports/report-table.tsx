"use client";

import { ArrowDown, ArrowUp } from "lucide-react";
import { useMemo, useState } from "react";
import { formatCell } from "@/lib/format";
import { sortRows } from "@/lib/report-utils";
import type { ReportColumn, ReportResult } from "@/lib/types";

const NUMERIC_TYPES = new Set(["number", "int", "integer", "double", "decimal", "duration", "percent", "seconds", "minutes", "hours"]);

function isNumericColumn(col: ReportColumn, rows: ReportResult["rows"]): boolean {
  if (NUMERIC_TYPES.has(col.type.toLowerCase())) return true;
  const sample = rows.find((r) => r[col.key] !== null && r[col.key] !== undefined);
  return sample !== undefined && typeof sample[col.key] === "number";
}

/** Generic renderer for any ReportResult: sticky header, click-to-sort, numeric alignment. */
export function ReportTable({ report }: { report: ReportResult }) {
  const [sort, setSort] = useState<{ key: string; dir: "asc" | "desc" } | null>(null);
  const rows = useMemo(() => (sort ? sortRows(report.rows, sort.key, sort.dir) : report.rows), [report.rows, sort]);

  return (
    <div className="table-wrap">
      <table className="data-table">
        <thead>
          <tr>
            {report.columns.map((c) => {
              const numeric = isNumericColumn(c, report.rows);
              const active = sort?.key === c.key;
              return (
                <th key={c.key} className={numeric ? "num" : undefined} aria-sort={active ? (sort.dir === "asc" ? "ascending" : "descending") : "none"}>
                  <button
                    type="button"
                    className="inline-flex items-center gap-1 uppercase tracking-[0.04em]"
                    onClick={() => setSort((prev) => (prev?.key === c.key ? { key: c.key, dir: prev.dir === "asc" ? "desc" : "asc" } : { key: c.key, dir: "asc" }))}
                  >
                    {c.label}
                    {active && (sort.dir === "asc" ? <ArrowUp size={11} aria-hidden /> : <ArrowDown size={11} aria-hidden />)}
                  </button>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) => (
            <tr key={i}>
              {report.columns.map((c) => (
                <td key={c.key} className={isNumericColumn(c, report.rows) ? "num" : undefined}>
                  {formatCell(row[c.key], c.type.toLowerCase())}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
