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
  await expect(page.getByText("AI Recipe Assistant").first()).toBeVisible();
});

test("mobile document exposes chat drawer", async ({ page, isMobile }) => {
  test.skip(!isMobile, "mobile only");
  await page.goto("/predictive_state_updates");
  await expect(page.getByText("AI Document Editor").first()).toBeVisible();
});

test("agentic generative ui keeps a single chat input after sending", async ({ page }) => {
  await page.goto("/agentic_generative_ui");

  const inputs = page.getByRole("textbox");
  await expect(inputs).toHaveCount(1);

  await page.getByText("Simple plan").click();

  const messageList = page.getByTestId("copilot-message-list");
  const taskProgressInTranscript = messageList.getByTestId("task-progress");
  await expect(taskProgressInTranscript).toHaveCount(1);
  await expect(page.getByTestId("task-progress")).toHaveCount(1);
  await expect(taskProgressInTranscript.getByTestId("task-step-text")).toHaveCount(5);
  await expect(taskProgressInTranscript).toContainText("0/5 Complete");
  await expect(taskProgressInTranscript).toContainText(/(?:1|2|3|4)\/5 Complete/, { timeout: 10000 });
  await expect(taskProgressInTranscript).toContainText("5/5 Complete", { timeout: 15000 });
  await expect(taskProgressInTranscript).not.toContainText("Processing...");

  await expect(inputs).toHaveCount(1);
});

function createSharedStateStream(recipeTitle: string, summary: string) {
  const snapshot = {
    recipe: {
      title: recipeTitle,
      skill_level: "Intermediate",
      cooking_time: "30 min",
      special_preferences: ["Spicy"],
      ingredients: [
        { icon: "🌶", name: "Doubanjiang", amount: "2 tbsp" },
        { icon: "🧈", name: "Silken tofu", amount: "400 g" },
      ],
      instructions: ["Bloom the doubanjiang in oil.", "Simmer gently and serve hot."],
    },
  };

  return [
    { type: "RUN_STARTED", threadId: "thread-shared-state", runId: "run-shared-state" },
    { type: "STATE_SNAPSHOT", snapshot },
    { type: "TEXT_MESSAGE_START", messageId: "assistant-summary", role: "assistant" },
    { type: "TEXT_MESSAGE_CONTENT", messageId: "assistant-summary", delta: summary },
    { type: "TEXT_MESSAGE_END", messageId: "assistant-summary" },
    { type: "RUN_FINISHED", threadId: "thread-shared-state", runId: "run-shared-state" },
  ].map((event) => `data: ${JSON.stringify(event)}\n\n`).join("");
}

test("shared state improve button sends a single improve message and updates the recipe", async ({ page }) => {
  const requests: any[] = [];

  await page.route("**/agui/shared_state", async (route) => {
    requests.push(route.request().postDataJSON());
    await route.fulfill({
      body: createSharedStateStream("宫保鸡丁 Pro", "Improved the current recipe."),
      contentType: "text/event-stream",
      status: 200,
    });
  });

  await page.goto("/shared_state?chat=open");

  const titleInput = page.getByTestId("recipe-title-input");
  await titleInput.fill("宫保鸡丁");
  await page.getByTestId("improve-button").click();

  const requestBody = requests[0].body ?? requests[0];
  await expect(titleInput).toHaveValue("宫保鸡丁 Pro");
  expect(requests).toHaveLength(1);
  expect(requestBody.state.recipe.title).toBe("宫保鸡丁");
  expect(requestBody.messages).toEqual([
    expect.objectContaining({
      content: "Improve the recipe",
      role: "user",
    }),
  ]);
});

test("shared state chat sends the user message with the latest recipe snapshot", async ({ page, isMobile }) => {
  test.skip(isMobile, "desktop only");
  const requests: any[] = [];

  await page.route("**/agui/shared_state", async (route) => {
    requests.push(route.request().postDataJSON());
    await route.fulfill({
      body: createSharedStateStream("麻婆豆腐", "Updated the recipe from the chat request."),
      contentType: "text/event-stream",
      status: 200,
    });
  });

  await page.goto("/shared_state?chat=open");
  await expect(page.getByRole("button", { name: "Close chat" })).toBeVisible();

  const titleInput = page.getByTestId("recipe-title-input");
  const chatInput = page.getByRole("textbox", { name: "Type a message..." });
  await titleInput.fill("宫保鸡丁");
  await chatInput.fill("麻婆豆腐");
  await chatInput.press("Enter");

  const requestBody = requests[0].body ?? requests[0];
  await expect(titleInput).toHaveValue("麻婆豆腐");
  expect(requests).toHaveLength(1);
  expect(requestBody.state.recipe.title).toBe("宫保鸡丁");
  expect(requestBody.messages).toEqual([
    expect.objectContaining({
      content: "麻婆豆腐",
      role: "user",
    }),
  ]);
});
