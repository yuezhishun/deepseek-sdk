import { diffWords } from "diff";
import MarkdownIt from "markdown-it";

const md = new MarkdownIt({ typographer: true, html: true });

export function fromMarkdown(text: string) {
  return md.render(text);
}

export function diffPartialText(oldText: string, newText: string, isComplete = false) {
  let oldTextToCompare = oldText;
  if (oldText.length > newText.length && !isComplete) {
    oldTextToCompare = oldText.slice(0, newText.length);
  }

  const changes = diffWords(oldTextToCompare, newText);
  let result = "";

  for (const part of changes) {
    if (part.added) {
      result += `<em>${part.value}</em>`;
    } else if (part.removed) {
      result += `<s>${part.value}</s>`;
    } else {
      result += part.value;
    }
  }

  if (oldText.length > newText.length && !isComplete) {
    result += oldText.slice(newText.length);
  }

  return result;
}
