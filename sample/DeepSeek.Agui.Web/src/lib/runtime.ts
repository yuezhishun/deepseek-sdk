import { markRaw, reactive } from "vue";
import { HttpAgent, type BaseEvent } from "@ag-ui/client";
import type { TraceRecord } from "./types";

const defaultAguiEndpoint = "http://localhost:5099/agui";
const aguiEndpoint = import.meta.env.VITE_AGUI_AGENT_URL ?? defaultAguiEndpoint;

type RuntimeState = {
  agentId: string;
  threadId: string;
  trace: TraceRecord[];
  agent: any;
  localAgents: Record<string, any>;
};

type RuntimeStore = {
  rawEvents: BaseEvent[];
  traceEvents: BaseEvent[];
  state: RuntimeState;
};

const runtimes = new Map<string, RuntimeStore>();

function summarize(event: BaseEvent): string {
  switch (event.type) {
    case "TEXT_MESSAGE_CONTENT":
    case "REASONING_MESSAGE_CONTENT":
    case "TOOL_CALL_ARGS":
      return String((event as any).delta ?? "");
    case "TOOL_CALL_START":
      return `${String((event as any).toolCallName)}(${String((event as any).toolCallId)})`;
    case "CUSTOM":
      return String((event as any).name ?? "custom");
    case "RUN_ERROR":
      return String((event as any).message ?? "error");
    default:
      return event.type;
  }
}

function toTraceRecord(event: BaseEvent, index: number): TraceRecord {
  return {
    id: `${index + 1}`,
    type: event.type,
    summary: summarize(event),
    payload: event,
  };
}

function shouldCompact(event: BaseEvent) {
  return event.type === "TEXT_MESSAGE_CONTENT" || event.type === "REASONING_MESSAGE_CONTENT";
}

type PendingContentEvent = {
  event: BaseEvent;
  type: string;
  messageId: string;
  delta: string;
};

function readMergeableContentEvent(event: BaseEvent): PendingContentEvent | null {
  if (!shouldCompact(event)) {
    return null;
  }

  const candidate = event as any;
  return {
    event,
    type: String(candidate.type ?? ""),
    messageId: String(candidate.messageId ?? ""),
    delta: String(candidate.delta ?? ""),
  };
}

function compactContentEvents(events: BaseEvent[]) {
  const compacted: BaseEvent[] = [];
  let pending: PendingContentEvent | null = null;

  const flushPending = () => {
    if (pending) {
      compacted.push({
        ...(pending.event as any),
        delta: pending.delta,
      });
      pending = null;
    }
  };

  for (const event of events) {
    const mergeableEvent = readMergeableContentEvent(event);

    if (!mergeableEvent) {
      flushPending();
      compacted.push(event);
      continue;
    }

    if (pending && pending.type === mergeableEvent.type && pending.messageId === mergeableEvent.messageId) {
      pending = {
        event: pending.event,
        type: pending.type,
        messageId: pending.messageId,
        delta: `${pending.delta}${mergeableEvent.delta}`,
      };
      continue;
    }

    flushPending();
    pending = mergeableEvent;
  }

  flushPending();
  return compacted;
}

function createRuntime(key: string, endpoint: string) {
  const threadId = crypto.randomUUID();
  const agentId = `${key}-${threadId}`;
  const agent = markRaw(new HttpAgent({
    agentId,
    threadId,
    url: endpoint,
    initialState: {},
  }));
  const localAgents = markRaw({ [agentId]: agent } as Record<string, any>);

  const state = reactive<RuntimeState>({
    agentId,
    threadId,
    trace: [],
    agent,
    localAgents,
  });
  const store: RuntimeStore = { rawEvents: [], traceEvents: [], state };

  agent.subscribe({
    onEvent: ({ event }) => {
      store.rawEvents = [...store.rawEvents, event];

      if (shouldCompact(event)) {
        store.traceEvents = compactContentEvents(store.rawEvents);
      } else {
        store.traceEvents = [...store.traceEvents, event];
      }

      state.trace = store.traceEvents.map(toTraceRecord);
    },
  });

  runtimes.set(key, store);
  return store;
}

export function useDemoRuntime(key: string, endpoint = aguiEndpoint) {
  const runtime = runtimes.get(key) ?? createRuntime(key, endpoint);
  const { state } = runtime;

  return {
    state,
    localAgents: state.localAgents,
    reset: () => {
      runtime.rawEvents = [];
      runtime.traceEvents = [];
      state.trace = [];
      state.agent.setMessages([]);
      state.agent.setState({});
    },
  };
}

export function getAguiEndpoint() {
  return aguiEndpoint;
}
