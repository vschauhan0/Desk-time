"use client";

import { useCallback, useState } from "react";
import { errorMessage } from "@/lib/api";

export interface ActionState<A extends unknown[], R> {
  run: (...args: A) => Promise<R | undefined>;
  pending: boolean;
  error: string | null;
  reset: () => void;
}

/** Wrap a mutation: tracks pending/error and never throws to the caller (returns undefined on failure). */
export function useAction<A extends unknown[], R>(fn: (...args: A) => Promise<R>): ActionState<A, R> {
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = useCallback(
    async (...args: A): Promise<R | undefined> => {
      setPending(true);
      setError(null);
      try {
        return await fn(...args);
      } catch (e) {
        setError(errorMessage(e));
        return undefined;
      } finally {
        setPending(false);
      }
    },
    [fn],
  );

  return { run, pending, error, reset: () => setError(null) };
}
