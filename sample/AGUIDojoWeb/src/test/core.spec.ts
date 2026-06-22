import { mount } from "@vue/test-utils";
import { nextTick } from "vue";
import { describe, expect, it, vi } from "vitest";
import ConfirmChanges from "@/components/document/ConfirmChanges.vue";
import HaikuDisplay from "@/components/haiku/HaikuDisplay.vue";
import StepCard from "@/components/steps/StepCard.vue";
import {
  applyPlanStateDelta,
  isLikelyPlanSummaryText,
  isPlanStateSnapshot,
  shouldHidePlanSummaryMessage,
  type PlanState,
} from "@/lib/agenticPlan";
import { diffPartialText } from "@/lib/document";
import { insertGeneratedHaiku, PLACEHOLDER_HAIKU, type Haiku } from "@/lib/haiku";
import { normalizeWeatherResult } from "@/lib/weather";
import { mergeRecipeUpdate } from "@/components/recipe/types";

vi.mock("@copilotkit/vue/v2", () => ({
  useFrontendTool: vi.fn(),
}));

describe("weather result normalization", () => {
  it("accepts JSON strings and snake-case wind fields", () => {
    expect(
      normalizeWeatherResult(
        '{"temperature":21,"conditions":"Cloudy","humidity":55,"wind_speed":8,"feels_like":20}',
      ),
    ).toEqual({
      temperature: 21,
      conditions: "Cloudy",
      humidity: 55,
      windSpeed: 8,
      feelsLike: 20,
    });
  });

  it("accepts object results", () => {
    expect(normalizeWeatherResult({ temperature: 10, conditions: "Clear", humidity: 30, windSpeed: 4 })).toMatchObject({
      windSpeed: 4,
      feelsLike: 10,
    });
  });
});

describe("haiku generation", () => {
  it("inserts generated haiku first and preserves the image path", () => {
    const next: Haiku = {
      japanese: ["春の海"],
      english: ["Spring ocean"],
      image_name: "Tokyo_Skyline_Night_Tokyo_Tower_Mount_Fuji_View.jpg",
      gradient: "linear-gradient(red, blue)",
    };

    const result = insertGeneratedHaiku([PLACEHOLDER_HAIKU], next);
    expect(result[0]).toEqual(next);
    expect(result).toHaveLength(1);
  });

  it("renders image paths under /images", async () => {
    const wrapper = mount(HaikuDisplay, {
      global: {
        stubs: {
          HaikuCard: false,
        },
      },
    });

    (wrapper.vm as any).addHaiku({
      japanese: ["富士山"],
      english: ["Mount Fuji"],
      image_name: "Mount_Fuji_Lake_Reflection_Cherry_Blossoms_Sakura_Spring.jpg",
      gradient: "",
    });
    await nextTick();

    expect(wrapper.find('[data-testid="haiku-image"]').attributes("src")).toBe(
      "/images/Mount_Fuji_Lake_Reflection_Cherry_Blossoms_Sakura_Spring.jpg",
    );
  });
});

describe("recipe updates", () => {
  it("merges edits into local recipe state", () => {
    const recipe = {
      title: "A",
      skill_level: "Beginner" as any,
      cooking_time: "5 min" as any,
      special_preferences: [],
      ingredients: [],
      instructions: ["one"],
    };

    expect(mergeRecipeUpdate(recipe, { title: "B", instructions: ["two"] })).toMatchObject({
      title: "B",
      instructions: ["two"],
    });
  });
});

describe("document review", () => {
  it("marks added and removed diff text", () => {
    expect(diffPartialText("hello old", "hello new", true)).toContain("<s>old</s>");
    expect(diffPartialText("hello old", "hello new", true)).toContain("<em>new</em>");
  });

  it("confirms and rejects changes", async () => {
    const respond = vi.fn();
    const wrapper = mount(ConfirmChanges, {
      props: {
        status: "executing",
        respond,
        onReject: vi.fn(),
        onConfirm: vi.fn(),
      },
    });

    await wrapper.find('[data-testid="confirm-button"]').trigger("click");
    expect(respond).toHaveBeenCalledWith({ accepted: true });
    expect(wrapper.find('[data-testid="status-display"]').text()).toContain("Accepted");
  });
});

describe("human-in-the-loop steps", () => {
  it("toggles steps and confirms selected steps", async () => {
    const respond = vi.fn();
    const wrapper = mount(StepCard, {
      props: {
        mode: "confirm",
        status: "executing",
        respond,
        steps: [
          { description: "one", status: "enabled" },
          { description: "two", status: "enabled" },
        ],
      },
    });

    await wrapper.findAll("input")[1].setValue(false);
    await wrapper.findAll("button").find((button) => button.text().includes("Confirm"))!.trigger("click");

    expect(respond).toHaveBeenCalledWith({
      accepted: true,
      steps: [{ description: "one", status: "enabled" }],
    });
  });
});

describe("agentic plan snapshots", () => {
  it("accepts valid step snapshots", () => {
    expect(
      isPlanStateSnapshot({
        steps: [
          { description: "Launch", status: "Pending" },
          { description: "Land", status: "completed" },
        ],
      }),
    ).toBe(true);
  });

  it("rejects invalid plan snapshots", () => {
    expect(isPlanStateSnapshot({})).toBe(false);
    expect(isPlanStateSnapshot({ steps: "nope" })).toBe(false);
    expect(isPlanStateSnapshot({ steps: [{ description: "Launch", status: "executing" }] })).toBe(false);
  });

  it("applies a single status delta incrementally", () => {
    const nextPlanState = applyPlanStateDelta(
      {
        steps: [
          { description: "One", status: "pending" },
          { description: "Two", status: "pending" },
        ],
      },
      [{ op: "replace", path: "/steps/0/status", value: "completed" }],
    );

    expect(nextPlanState.steps.map((step) => step.status)).toEqual(["completed", "pending"]);
  });

  it("applies sequential deltas without skipping intermediate progress", () => {
    const initialPlanState: PlanState = {
      steps: [
        { description: "One", status: "pending" },
        { description: "Two", status: "pending" },
        { description: "Three", status: "pending" },
      ],
    };

    const afterFirstDelta = applyPlanStateDelta(
      initialPlanState,
      [{ op: "replace", path: "/steps/0/status", value: "Completed" }],
    );
    const afterSecondDelta = applyPlanStateDelta(
      afterFirstDelta,
      [{ op: "replace", path: "/steps/1/status", value: "completed" }],
    );

    expect(afterFirstDelta.steps.map((step) => step.status)).toEqual(["completed", "pending", "pending"]);
    expect(afterSecondDelta.steps.map((step) => step.status)).toEqual(["completed", "completed", "pending"]);
  });

  it("detects markdown plan tables as redundant summary text", () => {
    expect(
      isLikelyPlanSummaryText(`
| Step | Status |
| --- | --- |
| Research Mars mission | pending |
| Launch spacecraft | completed |
      `),
    ).toBe(true);
  });

  it("does not hide a concise completion message when plan state exists", () => {
    expect(
      shouldHidePlanSummaryMessage(
        {
          role: "assistant",
          content: "The plan has been fully completed.",
        },
        true,
      ),
    ).toBe(false);
  });
});
