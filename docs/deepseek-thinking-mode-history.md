# DeepSeek Thinking Mode 多轮历史实现说明

本文说明本项目如何处理 DeepSeek thinking mode 下的多轮会话历史，重点回答下面这个问题：

- 普通多轮会话中，为什么思维链通常不会出现在下一轮请求里？
- 带工具调用的多轮会话中，为什么 `reasoning_content` 会被带回下一轮请求？

参考文档：

- DeepSeek Thinking Mode 指南: https://api-docs.deepseek.com/zh-cn/guides/thinking_mode

## 结论

本项目没有实现一个统一的显式分支，写成“普通多轮不带 `reasoning_content`，工具多轮才带 `reasoning_content`”。

实际实现方式是：

1. 适配层在把历史消息重新映射成下一轮请求时，如果某条 assistant 历史消息本身带有 `reasoning_content`，就会原样写回请求。
2. 工具调用续轮场景里，适配层会主动构造一条 assistant 工具调用消息，并把当前轮流式返回中累计到的 `reasoning_content` 一并附着到这条消息上。
3. 因此，工具调用多轮里会稳定看到 `reasoning_content` 出现在续轮请求中。
4. 普通多轮是否会带回 `reasoning_content`，取决于上层会话历史是否保留并再次传入了该字段，而不是由底层 `ChatClient` 单独决定。

## 代码位置

### 1. 底层模型已支持 `reasoning_content`

底层 OpenAI 兼容消息模型定义在：

- [src/DeepSeek/Chat/ChatMessage.cs](../src/DeepSeek/Chat/ChatMessage.cs)

关键字段：

```csharp
public string? ReasoningContent { get; set; }
```

说明 SDK 的 wire model 本身支持把 `reasoning_content` 发给 DeepSeek 接口，也支持从响应中读取该字段。

### 2. 请求映射时会回填 assistant 的 `reasoning_content`

OpenAI 兼容适配层的核心入口在：

- [src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatRequestMapper.cs](../src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatRequestMapper.cs)

`MapToChatRequest(...)` 会遍历历史消息，并调用 `MapMessages(...)`：

```csharp
foreach (var message in messages)
{
    foreach (var mappedMessage in MapMessages(message))
    {
        request.Messages.Add(mappedMessage);
    }
}
```

对 assistant 消息的映射逻辑是：

```csharp
var assistant = new WireChatMessage
{
    Role = "assistant",
    Content = message.Text,
    ReasoningContent = TryGetReasoningContent(message),
};
```

对应位置：

- [src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatRequestMapper.cs](../src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatRequestMapper.cs)

`TryGetReasoningContent(...)` 的行为：

1. 先从 `message.AdditionalProperties["reasoning_content"]` 读取。
2. 若不存在，再从 `message.Contents` 中的 `TextReasoningContent` 读取。

这意味着：

- 只要上层传入的 assistant 历史消息带了 `reasoning_content`，mapper 就会把它写进下一轮请求。
- mapper 本身不会根据“是否工具调用”主动删掉该字段。

### 3. 响应映射时会把 `reasoning_content` 放回 assistant 消息

非流式响应在 `MapToChatResponse(...)` 中处理：

- [src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatRequestMapper.cs](../src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatRequestMapper.cs)

相关逻辑：

```csharp
if (!string.IsNullOrWhiteSpace(choice?.Message?.ReasoningContent))
{
    assistant.AdditionalProperties = new AdditionalPropertiesDictionary
    {
        ["reasoning_content"] = choice.Message.ReasoningContent,
    };
    assistant.Contents.Add(new TextReasoningContent(choice.Message.ReasoningContent));
}
```

说明非流式返回的 `reasoning_content` 会被保存到适配层 assistant 消息中，供上层决定是否持久化到下一轮。

### 4. 工具调用续轮时，适配层会主动构造带 `reasoning_content` 的 assistant 历史消息

关键逻辑在：

- [src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatClient.cs](../src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatClient.cs)

在流式处理过程中：

1. 如果 chunk 中有 `reasoning_content`，会累积到 `StreamingAssistantTurnState._reasoning`。
2. 如果这一轮返回了 tool calls，适配层会进入本地续轮逻辑。
3. 续轮前会调用 `CreateAssistantToolCallMessage(toolCalls)` 构造一条 assistant 历史消息。

核心代码：

```csharp
if (!string.IsNullOrWhiteSpace(choice?.Delta?.ReasoningContent))
{
    turnState.AppendReasoning(choice.Delta.ReasoningContent!);
}
```

```csharp
conversation.Add(turnState.CreateAssistantToolCallMessage(toolCalls));
conversation.AddRange(toolResults.Select(static result => new AiChatMessage(ChatRole.Tool, [result])));
continue;
```

而 `CreateAssistantToolCallMessage(...)` 会这样写入：

```csharp
if (_reasoning.Length > 0)
{
    assistant.AdditionalProperties = new AdditionalPropertiesDictionary
    {
        ["reasoning_content"] = _reasoning.ToString(),
    };
}
```

这就是本项目中“带工具调用的多轮会话会把思维链带回请求”的直接实现点。

## 为什么普通多轮通常看不到这个行为

因为普通多轮场景下，是否把上一轮 assistant 的 `reasoning_content` 继续带入下一轮，请求链路依赖的是“上层会不会把这条带 `reasoning_content` 的 assistant 消息保存在会话里再传回来”。

而工具调用场景不同：

1. SDK 适配层自己负责构造 assistant 工具调用消息。
2. 这条消息在构造时会自动补上 `reasoning_content`。
3. 随后立刻把这条 assistant 消息和 tool result 一起加入 `conversation`，进入下一次服务调用。

所以工具调用续轮一定会发生这件事，而普通多轮不一定。

## 单元测试佐证

以下测试已经覆盖了工具调用续轮时的历史拼装：

- [test/Microsoft.Agents.AI.DeepSeek.UnitTests/DeepSeekAdapterTests.cs](../test/Microsoft.Agents.AI.DeepSeek.UnitTests/DeepSeekAdapterTests.cs)

重点测试：

- `AsAIAgent_PreservesSequentialToolHistoryAcrossStreamingRounds`
- `AsIChatClient_MapsGroupedToolResultsToSeparateWireMessages`
- `AsAIAgent_PreservesGroupedToolResultsInSingleContinuationRound`

这些测试都会断言 continuation request 中的 assistant 消息包含：

- `Role = "assistant"`
- `ReasoningContent = "..."`
- `ToolCalls = ...`

## 相关文件

- [src/DeepSeek/Chat/ChatMessage.cs](../src/DeepSeek/Chat/ChatMessage.cs)
- [src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatRequestMapper.cs](../src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatRequestMapper.cs)
- [src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatClient.cs](../src/Microsoft.Agents.AI.DeepSeek/DeepSeekChatClient.cs)
- [test/Microsoft.Agents.AI.DeepSeek.UnitTests/DeepSeekAdapterTests.cs](../test/Microsoft.Agents.AI.DeepSeek.UnitTests/DeepSeekAdapterTests.cs)

## 一句话总结

本项目的实现不是“按场景显式开关思维链回传”，而是“只要 assistant 历史消息里有 `reasoning_content` 就原样映射回请求”；工具调用多轮之所以稳定满足 DeepSeek 文档要求，是因为适配层在续轮时主动创建了那条带 `reasoning_content` 的 assistant 工具调用历史消息。
