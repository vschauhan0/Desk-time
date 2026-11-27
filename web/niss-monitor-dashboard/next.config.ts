import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Next 16 auto-writes AGENTS.md / CLAUDE.md into this folder on every dev start.
  // This repo keeps its AI-agent rules elsewhere, so disable the generation to keep the tree clean.
  agentRules: false,
};

export default nextConfig;
