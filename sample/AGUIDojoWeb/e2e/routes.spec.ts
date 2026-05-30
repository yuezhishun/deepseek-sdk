import { expect, test } from "@playwright/test";

const routes = [
  ["/agentic_chat", "background-container"],
  ["/backend_tool_rendering", "copilot-chat"],
  ["/human_in_the_loop", "copilot-chat"],
  ["/tool_based_generative_ui", "haiku-carousel"],
  ["/agentic_generative_ui", "copilot-chat"],
  ["/shared_state", "recipe-card"],
  ["/predictive_state_updates", "tiptap"],
] as const;

for (const [path, testIdOrClass] of routes) {
  test(`${path} loads`, async ({ page }) => {
    await page.goto(path);
    if (testIdOrClass === "copilot-chat") {
      await expect(page.getByTestId("copilot-chat")).toBeVisible();
    } else if (testIdOrClass === "tiptap") {
      await expect(page.locator(".tiptap").first()).toBeVisible();
    } else {
      await expect(page.getByTestId(testIdOrClass)).toBeVisible();
    }
  });
}

test("mobile shared state exposes chat drawer", async ({ page, isMobile }) => {
  test.skip(!isMobile, "mobile only");
  await page.goto("/shared_state");
  await expect(page.getByText("AI Recipe Assistant")).toBeVisible();
});

test("mobile document exposes chat drawer", async ({ page, isMobile }) => {
  test.skip(!isMobile, "mobile only");
  await page.goto("/predictive_state_updates");
  await expect(page.getByText("AI Document Editor")).toBeVisible();
});

test("agentic generative ui keeps a single chat input after sending", async ({ page }) => {
  await page.goto("/agentic_generative_ui");

  const inputs = page.getByRole("textbox");
  await expect(inputs).toHaveCount(1);

  await inputs.first().fill("Please build a plan to go to mars in 5 steps.");
  await inputs.first().press("Enter");

  await expect(inputs).toHaveCount(1);
});
