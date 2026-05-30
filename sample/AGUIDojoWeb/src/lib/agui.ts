import { HttpAgent, type AbstractAgent } from "@ag-ui/client";

export const AGENT_IDS = {
  agenticChat: "agentic_chat",
  backendToolRendering: "backend_tool_rendering",
  humanInTheLoop: "human_in_the_loop",
  toolBasedGenerativeUi: "tool_based_generative_ui",
  agenticGenerativeUi: "agentic_generative_ui",
  sharedState: "shared_state",
  predictiveStateUpdates: "predictive_state_updates",
} as const;

export type AgentId = (typeof AGENT_IDS)[keyof typeof AGENT_IDS];

export const AGUI_BASE_URL = import.meta.env.VITE_AGUI_BASE_URL || "/agui";
const agentCache = new Map<AgentId, AbstractAgent>();

export function agentUrl(agentId: AgentId): string {
  return `${AGUI_BASE_URL}/${agentId}`;
}

export function getAgent(agentId: AgentId): AbstractAgent {
  const existingAgent = agentCache.get(agentId);
  if (existingAgent) {
    return existingAgent;
  }

  const agent = new HttpAgent({ url: agentUrl(agentId) });
  agentCache.set(agentId, agent);

  return agent;
}

export function createAgentRecord(agentId: AgentId): Record<AgentId, AbstractAgent> {
  return {
    [agentId]: getAgent(agentId),
  } as Record<AgentId, AbstractAgent>;
}
