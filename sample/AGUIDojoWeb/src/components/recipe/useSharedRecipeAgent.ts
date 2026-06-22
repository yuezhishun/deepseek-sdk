import { computed, reactive, shallowRef, toRaw, watch, type ShallowRef } from "vue";
import type { AbstractAgent } from "@ag-ui/client";
import { useAgent, useCopilotKit, UseAgentUpdate } from "@copilotkit/vue/v2";
import { AGENT_IDS } from "@/lib/agui";
import {
  INITIAL_RECIPE_STATE,
  mergeRecipeUpdate,
  type Recipe,
  type RecipeAgentState,
} from "./types";

function normalizeRecipeValue(value: unknown) {
  if (typeof value === "string") {
    return value.replace(/\\n/g, "\n");
  }

  return value;
}

export function createRecipeSnapshot(recipe: Recipe): Recipe {
  return structuredClone(toRaw(recipe));
}

export function applyRecipeSnapshot(recipe: Recipe, nextRecipe: Recipe): string[] {
  const nextChangedKeys: string[] = [];

  for (const key of Object.keys(recipe) as (keyof Recipe)[]) {
    const nextValue = normalizeRecipeValue(nextRecipe[key]);
    if (nextValue != null && JSON.stringify(nextValue) !== JSON.stringify(recipe[key])) {
      (recipe as Record<keyof Recipe, unknown>)[key] = nextValue as Recipe[keyof Recipe];
      nextChangedKeys.push(key);
    }
  }

  return nextChangedKeys;
}

interface SharedRecipeAgentOptions {
  agentRef?: ShallowRef<AbstractAgent | null>;
}

export function useSharedRecipeAgent(options: SharedRecipeAgentOptions = {}) {
  const resolvedAgent = options.agentRef ?? useAgent({
    agentId: AGENT_IDS.sharedState,
    updates: [UseAgentUpdate.OnStateChanged, UseAgentUpdate.OnRunStatusChanged],
  }).agent;
  const { copilotkit } = useCopilotKit();

  const recipe = reactive<Recipe>(structuredClone(INITIAL_RECIPE_STATE.recipe));
  const changedKeys = shallowRef<string[]>([]);
  const isApplyingAgentSnapshot = shallowRef(false);

  const agentState = computed(() => resolvedAgent.value?.state as RecipeAgentState | undefined);
  const isLoading = computed(() => Boolean(resolvedAgent.value?.isRunning));

  function syncRecipeStateToAgent(nextRecipe: Recipe) {
    resolvedAgent.value?.setState({ recipe: nextRecipe });
  }

  function ensureAgentStateInitialized() {
    if (!resolvedAgent.value) {
      return;
    }

    if (agentState.value?.recipe) {
      applyAgentRecipeSnapshot(agentState.value.recipe);
      return;
    }

    syncRecipeStateToAgent(createRecipeSnapshot(recipe));
  }

  function updateRecipe(partialRecipe: Partial<Recipe>) {
    const nextRecipe = mergeRecipeUpdate(recipe, partialRecipe);
    Object.assign(recipe, nextRecipe);
    syncRecipeStateToAgent(createRecipeSnapshot(recipe));
  }

  function applyAgentRecipeSnapshot(nextRecipe: Recipe) {
    isApplyingAgentSnapshot.value = true;
    try {
      const nextChangedKeys = applyRecipeSnapshot(recipe, nextRecipe);
      changedKeys.value = nextChangedKeys.length > 0 ? nextChangedKeys : isLoading.value ? changedKeys.value : [];
    } finally {
      isApplyingAgentSnapshot.value = false;
    }
  }

  function improveRecipe() {
    if (isLoading.value || !resolvedAgent.value) {
      return;
    }

    resolvedAgent.value.addMessage({
      id: crypto.randomUUID(),
      role: "user",
      content: "Improve the recipe",
    });
    void copilotkit.value.runAgent({ agent: resolvedAgent.value });
  }

  watch(resolvedAgent, ensureAgentStateInitialized, { immediate: true });

  watch(
    () => agentState.value?.recipe,
    (nextRecipe) => {
      if (!nextRecipe || isApplyingAgentSnapshot.value) {
        return;
      }

      applyAgentRecipeSnapshot(nextRecipe);
    },
    { deep: true },
  );

  watch(
    resolvedAgent,
    (agent, _previousAgent, onCleanup) => {
      if (!agent) {
        return;
      }

      const subscription = agent.subscribe({
        onRunFinishedEvent: ({ state }) => {
          const nextRecipe = (state as RecipeAgentState | undefined)?.recipe;
          if (nextRecipe) {
            applyAgentRecipeSnapshot(nextRecipe);
          }
        },
        onStateSnapshotEvent: ({ event }) => {
          const nextRecipe = (event.snapshot as RecipeAgentState | undefined)?.recipe;
          if (nextRecipe) {
            applyAgentRecipeSnapshot(nextRecipe);
          }
        },
      });

      onCleanup(() => subscription.unsubscribe());
    },
    { immediate: true },
  );

  return {
    agent: resolvedAgent,
    changedKeys,
    improveRecipe,
    isLoading,
    recipe,
    updateRecipe,
  };
}
