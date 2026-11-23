"use client";

import { Inbox, RefreshCw, ServerCrash, ShieldAlert } from "lucide-react";
import type { ReactNode } from "react";
import { ApiError, errorMessage, isNetworkError } from "@/lib/api";
import { Button, Skeleton } from "./primitives";

/** Friendly "server unreachable" state, used full-page (login/app shell) and inline (a failed data load). */
export function ServerUnreachable({ url, onRetry, fullPage = false }: { url: string; onRetry?: () => void; fullPage?: boolean }) {
  const health = `${url}/api/v1/health`;
  return (
    <div className={fullPage ? "flex min-h-screen items-center justify-center p-6" : "flex justify-center p-6"}>
      <div className="card max-w-xl p-6 text-center" role="alert">
        <ServerCrash className="mx-auto text-danger" size={36} aria-hidden />
        <h2 className="mt-3 text-lg font-semibold">Cannot reach NISS server</h2>
        <p className="mt-2 text-[13px] text-muted">
          Cannot reach NISS server at <span className="mono text-fg">{url}</span> - if you use a self-signed certificate
          open{" "}
          <a className="mono break-all text-brand underline" href={health} target="_blank" rel="noopener noreferrer">
            {health}
          </a>{" "}
          once and accept it.
        </p>
        <p className="mt-2 text-[12px] text-faint">
          The API address comes from <span className="mono">/runtime-config.json</span> (<span className="mono">apiBaseUrl</span>)
          or defaults to <span className="mono">https://&lt;this host&gt;:8443</span>.
        </p>
        {onRetry && (
          <div className="mt-4">
            <Button variant="primary" icon={RefreshCw} onClick={onRetry}>
              Try again
            </Button>
          </div>
        )}
      </div>
    </div>
  );
}

export function ErrorBox({ error, onRetry }: { error: Error; onRetry?: () => void }) {
  if (isNetworkError(error)) return <ServerUnreachable url={error.baseUrl} onRetry={onRetry} />;
  const forbidden = error instanceof ApiError && error.status === 403;
  return (
    <div className="card flex items-start gap-3 border-danger/40 p-4" role="alert">
      <ShieldAlert className="mt-0.5 shrink-0 text-danger" size={18} aria-hidden />
      <div className="min-w-0 flex-1">
        <p className="font-semibold">{forbidden ? "You do not have permission to view this" : "Could not load data"}</p>
        <p className="mt-0.5 break-words text-[13px] text-muted">{errorMessage(error)}</p>
      </div>
      {onRetry && !forbidden && (
        <Button size="sm" icon={RefreshCw} onClick={onRetry}>
          Retry
        </Button>
      )}
    </div>
  );
}

export function EmptyState({ title, hint, icon }: { title: string; hint?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-1 px-4 py-10 text-center">
      <div className="text-faint">{icon ?? <Inbox size={28} aria-hidden />}</div>
      <p className="font-medium">{title}</p>
      {hint && <p className="max-w-md text-[13px] text-muted">{hint}</p>}
    </div>
  );
}

export function TableSkeleton({ rows = 6, cols = 5 }: { rows?: number; cols?: number }) {
  return (
    <div className="card space-y-2 p-4" aria-busy="true" aria-label="Loading">
      {Array.from({ length: rows }, (_, r) => (
        <div key={r} className="flex gap-3">
          {Array.from({ length: cols }, (_, c) => (
            <Skeleton key={c} className="h-5 flex-1" />
          ))}
        </div>
      ))}
    </div>
  );
}

interface DataStateProps {
  loading: boolean;
  error: Error | undefined;
  isEmpty?: boolean;
  onRetry?: () => void;
  emptyTitle?: string;
  emptyHint?: ReactNode;
  skeleton?: ReactNode;
  children: ReactNode;
}

/** Standard loading / error / empty wrapper: children render only when there is data to show. */
export function DataState({ loading, error, isEmpty, onRetry, emptyTitle = "Nothing to show", emptyHint, skeleton, children }: DataStateProps) {
  if (error) return <ErrorBox error={error} onRetry={onRetry} />;
  if (loading) return <>{skeleton ?? <TableSkeleton />}</>;
  if (isEmpty)
    return (
      <div className="card">
        <EmptyState title={emptyTitle} hint={emptyHint} />
      </div>
    );
  return <>{children}</>;
}
