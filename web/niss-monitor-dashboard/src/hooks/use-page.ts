"use client";

import { useCallback, useState } from "react";

/**
 * Page number that resets to 1 whenever `signature` (a serialisation of the active filters) changes.
 * Implemented without an effect: the stored page is only honoured for the signature it was set under.
 */
export function usePage(signature: string): [number, (p: number) => void] {
  const [state, setState] = useState({ signature, page: 1 });
  const page = state.signature === signature ? state.page : 1;
  const setPage = useCallback((p: number) => setState({ signature, page: p }), [signature]);
  return [page, setPage];
}
