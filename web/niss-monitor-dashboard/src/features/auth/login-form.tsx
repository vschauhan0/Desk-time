"use client";

import { LogIn } from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState, type FormEvent } from "react";
import { Brand } from "@/components/layout/brand";
import { Button, Field, Notice } from "@/components/ui/primitives";
import { ServerUnreachable } from "@/components/ui/states";
import { errorMessage, isNetworkError } from "@/lib/api";
import { useAuth } from "@/providers/auth-provider";

/** Only same-origin relative paths are accepted as post-login targets (no open redirect). */
export function safeNext(next: string | null): string {
  return next && next.startsWith("/") && !next.startsWith("//") && !next.startsWith("/login") ? next : "/";
}

export function LoginForm() {
  const { status, login, unreachableUrl, retry } = useAuth();
  const router = useRouter();
  const params = useSearchParams();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const target = safeNext(params.get("next"));

  useEffect(() => {
    if (status === "authenticated") router.replace(target);
  }, [status, router, target]);

  if (unreachableUrl && status !== "authenticated") {
    return <ServerUnreachable url={unreachableUrl} onRetry={retry} fullPage />;
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(username.trim(), password);
      router.replace(target);
    } catch (err) {
      setError(isNetworkError(err) ? err.message : errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center p-4">
      <form onSubmit={submit} className="card w-full max-w-sm space-y-4 p-6" aria-label="Sign in">
        <div className="flex justify-center pb-1">
          <Brand />
        </div>
        <div className="text-center">
          <h1 className="text-lg font-semibold">Administrator sign in</h1>
          <p className="mt-0.5 text-[12.5px] text-muted">Employee PC monitoring - authorised administrators only.</p>
        </div>
        {error && <Notice tone="danger">{error}</Notice>}
        <Field label="Username">
          <input className="field" autoComplete="username" autoFocus required value={username} onChange={(e) => setUsername(e.target.value)} />
        </Field>
        <Field label="Password">
          <input className="field" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Button type="submit" variant="primary" icon={LogIn} loading={busy} className="w-full">
          Sign in
        </Button>
        <p className="text-center text-[11.5px] text-faint">
          All administrator access is recorded in the audit log.
        </p>
      </form>
    </div>
  );
}
