/** NISS brand mark: simple inline wordmark, no external assets. */
export function BrandMark({ size = 28 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" aria-hidden>
      <rect width="32" height="32" rx="8" fill="var(--brand)" />
      <path d="M8 23V9h3.2l9.6 9.6V9H24v14h-3.2L11.2 13.4V23z" fill="var(--on-brand)" />
    </svg>
  );
}

export function Brand({ compact = false }: { compact?: boolean }) {
  return (
    <span className="flex items-center gap-2.5">
      <BrandMark />
      {!compact && (
        <span className="leading-none">
          <span className="block text-[15px] font-extrabold tracking-[0.14em]">NISS</span>
          <span className="mt-0.5 block text-[10px] font-medium uppercase tracking-[0.18em] text-muted">PC Monitor</span>
        </span>
      )}
    </span>
  );
}
