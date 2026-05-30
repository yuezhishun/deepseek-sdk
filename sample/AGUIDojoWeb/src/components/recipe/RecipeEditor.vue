<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from "vue";
import { useAgent, useConfigureSuggestions, useCopilotKit, UseAgentUpdate } from "@copilotkit/vue/v2";
import { AGENT_IDS } from "@/lib/agui";
import { useMobileView } from "@/composables/useMobileView";
import {
  cookingTimeValues,
  INITIAL_RECIPE_STATE,
  mergeRecipeUpdate,
  SkillLevel,
  SpecialPreferences,
  type Ingredient,
  type Recipe,
  type RecipeAgentState,
} from "./types";

const { isMobile } = useMobileView();
const { agent } = useAgent({
  agentId: AGENT_IDS.sharedState,
  updates: [UseAgentUpdate.OnStateChanged, UseAgentUpdate.OnRunStatusChanged],
});
const { copilotkit } = useCopilotKit();

useConfigureSuggestions({
  suggestions: [
    { title: "Create Italian recipe", message: "Create a delicious Italian pasta recipe." },
    { title: "Make it healthier", message: "Make the recipe healthier with more vegetables." },
    { title: "Suggest variations", message: "Suggest some creative variations of this recipe." },
  ],
  available: "always",
});

const recipe = reactive<Recipe>(structuredClone(INITIAL_RECIPE_STATE.recipe));
const editingInstructionIndex = ref<number | null>(null);
const changedKeys = ref<string[]>([]);

const agentState = computed(() => agent.value?.state as RecipeAgentState | undefined);
const isLoading = computed(() => Boolean(agent.value?.isRunning));

function setAgentState(state: RecipeAgentState) {
  agent.value?.setState(state);
}

function updateRecipe(partialRecipe: Partial<Recipe>) {
  Object.assign(recipe, mergeRecipeUpdate(recipe, partialRecipe));
  setAgentState({ recipe: structuredClone(recipe) });
}

watch(
  agent,
  () => {
    if (!agentState.value?.recipe) {
      setAgentState(INITIAL_RECIPE_STATE);
    }
  },
  { immediate: true },
);

watch(
  () => agentState.value?.recipe,
  (nextRecipe) => {
    if (!nextRecipe) return;
    const nextChangedKeys: string[] = [];

    for (const key of Object.keys(recipe) as (keyof Recipe)[]) {
      let agentValue = nextRecipe[key] as any;
      if (typeof agentValue === "string") {
        agentValue = agentValue.replace(/\\n/g, "\n");
      }
      if (agentValue != null && JSON.stringify(agentValue) !== JSON.stringify(recipe[key])) {
        (recipe as any)[key] = agentValue;
        nextChangedKeys.push(key);
      }
    }

    changedKeys.value = nextChangedKeys.length > 0 ? nextChangedKeys : isLoading.value ? changedKeys.value : [];
  },
  { deep: true },
);

function handleDietaryChange(preference: string, checked: boolean) {
  updateRecipe({
    special_preferences: checked
      ? [...recipe.special_preferences, preference]
      : recipe.special_preferences.filter((item) => item !== preference),
  });
}

function updateIngredient(index: number, field: keyof Ingredient, value: string) {
  const ingredients = recipe.ingredients.map((ingredient, ingredientIndex) =>
    ingredientIndex === index ? { ...ingredient, [field]: value } : ingredient,
  );
  updateRecipe({ ingredients });
}

function addIngredient() {
  updateRecipe({
    ingredients: [...recipe.ingredients, { icon: "🍴", name: "", amount: "" }],
  });
}

function removeIngredient(index: number) {
  updateRecipe({ ingredients: recipe.ingredients.filter((_, itemIndex) => itemIndex !== index) });
}

function addInstruction() {
  const newIndex = recipe.instructions.length;
  updateRecipe({ instructions: [...recipe.instructions, ""] });
  editingInstructionIndex.value = newIndex;
  void nextTick(() => {
    const textareas = document.querySelectorAll<HTMLTextAreaElement>(".instructions-container textarea");
    textareas[textareas.length - 1]?.focus();
  });
}

function updateInstruction(index: number, value: string) {
  const instructions = recipe.instructions.map((instruction, instructionIndex) =>
    instructionIndex === index ? value : instruction,
  );
  updateRecipe({ instructions });
}

function removeInstruction(index: number) {
  updateRecipe({
    instructions: recipe.instructions.filter((_, instructionIndex) => instructionIndex !== index),
  });
}

function improveRecipe() {
  if (isLoading.value || !agent.value) return;
  agent.value.addMessage({
    id: crypto.randomUUID(),
    role: "user",
    content: "Improve the recipe",
  });
  void copilotkit.value.runAgent({ agent: agent.value });
}

defineExpose({ recipe, updateRecipe });
</script>

<template>
  <form
    data-testid="recipe-card"
    class="recipe-card"
    :style="isMobile ? { marginBottom: '100px' } : {}"
  >
    <div class="recipe-header">
      <input
        class="recipe-title-input"
        type="text"
        :value="recipe.title"
        @input="updateRecipe({ title: ($event.target as HTMLInputElement).value })"
      />
      <div class="recipe-meta">
        <div class="meta-item">
          <span class="meta-icon">🕒</span>
          <select
            class="meta-select"
            :value="cookingTimeValues.find((time) => time.label === recipe.cooking_time)?.value ?? 3"
            @change="updateRecipe({ cooking_time: cookingTimeValues[Number(($event.target as HTMLSelectElement).value)].label })"
          >
            <option v-for="time in cookingTimeValues" :key="time.value" :value="time.value">
              {{ time.label }}
            </option>
          </select>
        </div>
        <div class="meta-item">
          <span class="meta-icon">🏆</span>
          <select
            class="meta-select"
            :value="recipe.skill_level"
            @change="updateRecipe({ skill_level: ($event.target as HTMLSelectElement).value as SkillLevel })"
          >
            <option v-for="level in Object.values(SkillLevel)" :key="level" :value="level">
              {{ level }}
            </option>
          </select>
        </div>
      </div>
    </div>

    <div class="section-container relative">
      <span v-if="changedKeys.includes('special_preferences')" class="ping-animation">
        <span class="ping-circle"></span>
        <span class="ping-dot"></span>
      </span>
      <h2 class="section-title">Dietary Preferences</h2>
      <div class="dietary-options">
        <label
          v-for="option in Object.values(SpecialPreferences)"
          :key="option"
          class="dietary-option"
        >
          <input
            type="checkbox"
            :checked="recipe.special_preferences.includes(option)"
            @change="handleDietaryChange(option, ($event.target as HTMLInputElement).checked)"
          />
          <span>{{ option }}</span>
        </label>
      </div>
    </div>

    <div class="section-container relative">
      <span v-if="changedKeys.includes('ingredients')" class="ping-animation">
        <span class="ping-circle"></span>
        <span class="ping-dot"></span>
      </span>
      <div class="section-header">
        <h2 class="section-title">Ingredients</h2>
        <button data-testid="add-ingredient-button" type="button" class="add-button" @click="addIngredient">
          + Add Ingredient
        </button>
      </div>
      <div data-testid="ingredients-container" class="ingredients-container">
        <div
          v-for="(ingredient, index) in recipe.ingredients"
          :key="index"
          data-testid="ingredient-card"
          class="ingredient-card"
        >
          <div class="ingredient-icon">{{ ingredient.icon || "🍴" }}</div>
          <div class="ingredient-content">
            <input
              class="ingredient-name-input"
              type="text"
              :value="ingredient.name"
              placeholder="Ingredient name"
              @input="updateIngredient(index, 'name', ($event.target as HTMLInputElement).value)"
            />
            <input
              class="ingredient-amount-input"
              type="text"
              :value="ingredient.amount"
              placeholder="Amount"
              @input="updateIngredient(index, 'amount', ($event.target as HTMLInputElement).value)"
            />
          </div>
          <button class="remove-button" type="button" aria-label="Remove ingredient" @click="removeIngredient(index)">
            ×
          </button>
        </div>
      </div>
    </div>

    <div class="section-container relative">
      <span v-if="changedKeys.includes('instructions')" class="ping-animation">
        <span class="ping-circle"></span>
        <span class="ping-dot"></span>
      </span>
      <div class="section-header">
        <h2 class="section-title">Instructions</h2>
        <button type="button" class="add-step-button" @click="addInstruction">+ Add Step</button>
      </div>
      <div data-testid="instructions-container" class="instructions-container">
        <div v-for="(instruction, index) in recipe.instructions" :key="index" class="instruction-item">
          <div class="instruction-number">{{ index + 1 }}</div>
          <div v-if="index < recipe.instructions.length - 1" class="instruction-line" />
          <div
            class="instruction-content"
            :class="
              editingInstructionIndex === index
                ? 'instruction-content-editing'
                : 'instruction-content-default'
            "
            @click="editingInstructionIndex = index"
          >
            <textarea
              class="instruction-textarea"
              :value="instruction"
              :placeholder="!instruction ? 'Enter cooking instruction...' : ''"
              @input="updateInstruction(index, ($event.target as HTMLTextAreaElement).value)"
              @focus="editingInstructionIndex = index"
              @blur="editingInstructionIndex = null"
            />
            <button
              class="instruction-delete-btn remove-button"
              type="button"
              aria-label="Remove instruction"
              @click.stop="removeInstruction(index)"
            >
              ×
            </button>
          </div>
        </div>
      </div>
    </div>

    <div class="action-container">
      <button
        data-testid="improve-button"
        type="button"
        class="improve-button"
        :class="{ loading: isLoading }"
        :disabled="isLoading"
        @click="improveRecipe"
      >
        {{ isLoading ? "Please Wait..." : "Improve with AI" }}
      </button>
    </div>
  </form>
</template>
