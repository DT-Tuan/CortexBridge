<script lang="ts">
	import type { SessionMessage, ContentBlock } from '$lib/api/types';
	import { renderMarkdown } from '$lib/utils/markdown';
	import { parseLocalCommand } from '$lib/utils/commands';
	import { copyableCodeBlocks } from '$lib/utils/code-copy';
	import { highlightCodeBlocks } from '$lib/utils/code-highlight';

	let { msg }: { msg: SessionMessage } = $props();

	function isContentBlocks(c: unknown): c is ContentBlock[] {
		return Array.isArray(c);
	}

	// One-line summary next to a collapsed tool name (command / file / pattern) —
	// parity with the companion extension's ToolCallCard + Anthropic CC ext.
	function toolSummary(input: unknown): string {
		if (input == null) return '';
		if (typeof input === 'string') return firstLine(input);
		if (typeof input === 'object') {
			const o = input as Record<string, unknown>;
			const pick = o.command ?? o.file_path ?? o.path ?? o.pattern ?? o.url ?? o.description;
			if (typeof pick === 'string') return firstLine(pick);
		}
		return '';
	}
	function firstLine(s: string): string {
		const ln = s.split('\n')[0].trim();
		return ln.length > 72 ? ln.slice(0, 69) + '…' : ln;
	}

	function bashOf(input: unknown): { command: string; description?: string } | null {
		if (input && typeof input === 'object') {
			const o = input as Record<string, unknown>;
			if (typeof o.command === 'string') {
				return {
					command: o.command,
					description: typeof o.description === 'string' ? o.description : undefined
				};
			}
		}
		return null;
	}

	type Todo = { content: string; status: 'pending' | 'in_progress' | 'completed'; activeForm?: string };
	function todoItems(input: unknown): Todo[] {
		if (input && typeof input === 'object') {
			const t = (input as { todos?: unknown }).todos;
			if (Array.isArray(t)) return t as Todo[];
		}
		return [];
	}

	// Slash commands typed by user (e.g. /clear) come through as user records wrapped
	// in <local-command-caveat>...</local-command-caveat>. Detect first so we render
	// them as a small badge instead of a raw XML bubble.
	const localCommand = $derived(parseLocalCommand(msg.content));

	// CC 2.1.x: tool_result records carry userType="external"; detect by content shape only.
	const isToolResult = $derived(
		msg.role === 'user' &&
			isContentBlocks(msg.content) &&
			msg.content.some((b) => b.type === 'tool_result')
	);
	const isUser = $derived(msg.role === 'user' && !isToolResult && !localCommand);
	const isAssistant = $derived(msg.role === 'assistant');
	const isSummary = $derived(msg.role === 'summary');
	const isSystem = $derived(msg.role === 'system');
	const isInternalNoise = $derived(msg.kind === 'unknown');

	// /compact divider. Backend (JsonlReader) emits kind:"compact" with a stable
	// text tuple "trigger|preTokens|postTokens" (the huge isCompactSummary
	// context-seed record is suppressed as kind:"unknown"). Format here so i18n
	// + number formatting stay in the PWA.
	const isCompact = $derived(msg.kind === 'compact');
	const compactInfo = $derived.by(() => {
		if (!isCompact || !msg.text) return '';
		const [trigger, pre, post] = msg.text.split('|');
		const k = (n: string) => {
			const v = Number(n);
			return Number.isFinite(v) && v > 0 ? `${Math.round(v / 1000)}k` : null;
		};
		const a = k(pre);
		const b = k(post);
		const parts: string[] = [];
		if (trigger && trigger !== '?') parts.push(trigger);
		if (a && b) parts.push(`${a}→${b} token`);
		return parts.length ? ` (${parts.join(' · ')})` : '';
	});
</script>

{#if localCommand && localCommand.name}
	<!-- Slash command typed by user — render as a subtle badge, right-aligned to match user side. -->
	<div class="flex justify-end">
		<div
			class="text-xs text-[var(--color-text-dim)] bg-[var(--color-surface)]/40 border border-[var(--color-border)]/40 rounded-full px-3 py-1 inline-flex items-center gap-1.5"
		>
			<span class="opacity-60">▶</span>
			<span class="font-mono">{localCommand.name}</span>
			{#if localCommand.args}
				<span class="opacity-70 truncate max-w-[160px]">{localCommand.args}</span>
			{/if}
		</div>
	</div>
{:else if localCommand}
	<!-- Caveat-only header record — hide (the paired command record will show the badge). -->
{:else if isInternalNoise}
	<!-- CC internal records (permission-mode, queue-operation, etc) — hide silently -->
{:else if isCompact}
	<!-- /compact boundary — one-line divider in place of the huge summary bubble -->
	<div
		class="my-3 flex items-center gap-2 select-none text-[10px] text-[var(--color-text-dim)] opacity-70"
		aria-label="Đã nén hội thoại"
	>
		<span class="h-px flex-1 bg-[var(--color-border)]"></span>
		<span class="shrink-0">🗜 Đã nén hội thoại{compactInfo}</span>
		<span class="h-px flex-1 bg-[var(--color-border)]"></span>
	</div>
{:else if isSummary}
	<div class="text-xs text-[var(--color-text-dim)] italic px-2 py-2 opacity-70">
		📋 {msg.text ?? ''}
	</div>
{:else if isSystem}
	<div class="text-xs text-[var(--color-text-dim)] italic px-2 py-1 opacity-70">
		ⓘ {msg.text ?? ''}
	</div>
{:else if isToolResult && isContentBlocks(msg.content)}
	<details class="text-xs">
		<summary
			class="cursor-pointer text-[var(--color-text-dim)] py-1 select-none flex items-center gap-1.5"
		>
			<span class="opacity-60">↳</span>
			<span>tool result</span>
		</summary>
		<div
			class="mt-1 p-2.5 rounded-lg bg-[var(--color-surface)]/50 font-mono text-xs text-[var(--color-text-dim)] whitespace-pre-wrap break-words overflow-auto max-h-72"
		>
			{#each msg.content as block, i (i)}
				{#if block.type === 'tool_result'}
					{typeof block.content === 'string' ? block.content : JSON.stringify(block.content, null, 2)}
				{/if}
			{/each}
		</div>
	</details>
{:else if isUser}
	<!-- User message: right-aligned subtle bubble (Claude mobile style — slate not cyan) -->
	<div class="flex justify-end">
		<div
			class="max-w-[88%] rounded-2xl px-4 py-2.5 bg-[var(--color-surface)] border border-[var(--color-border)]/50 text-[15px] leading-relaxed"
		>
			{#if typeof msg.content === 'string'}
				<p class="whitespace-pre-wrap break-words">{msg.content}</p>
			{:else if isContentBlocks(msg.content)}
				{#each msg.content as block, i (i)}
					{#if block.type === 'text'}
						<p class="whitespace-pre-wrap break-words">{block.text}</p>
					{/if}
				{/each}
			{/if}
		</div>
	</div>
{:else if isAssistant}
	<!-- Assistant message: NO bubble container — plain text with markdown, full width (Claude mobile style) -->
	<div
		class="cb-md text-[15px] leading-relaxed text-[var(--color-text)] px-1"
		use:copyableCodeBlocks
		use:highlightCodeBlocks
	>
		{#if typeof msg.content === 'string'}
			<!-- eslint-disable-next-line svelte/no-at-html-tags -->
			{@html renderMarkdown(msg.content)}
		{:else if isContentBlocks(msg.content)}
			{#each msg.content as block, i (i)}
				{#if block.type === 'text'}
					<!-- eslint-disable-next-line svelte/no-at-html-tags -->
					{@html renderMarkdown(block.text)}
				{:else if block.type === 'tool_use' && block.name === 'TodoWrite'}
					{@const todos = todoItems(block.input)}
					{#if todos.length > 0}
						<div
							class="my-1.5 rounded-lg border border-[var(--color-border)]/50 bg-[var(--color-bg)] overflow-hidden text-sm"
						>
							<div
								class="flex items-center gap-1.5 px-3 py-1.5 border-b border-[var(--color-border)]/40 text-[var(--color-text-dim)] text-xs"
							>
								<span class="text-[var(--color-accent)]">☑</span>
								<span class="font-semibold text-[var(--color-text)]">Todos</span>
								<span class="ml-auto font-mono">
									{todos.filter((t) => t.status === 'completed').length}/{todos.length}
								</span>
							</div>
							<ul class="list-none m-0 p-3 flex flex-col gap-1">
								{#each todos as t, ti (ti)}
									<li class="flex items-start gap-2 leading-relaxed">
										<span
											class="shrink-0 w-3 text-center font-mono text-xs {t.status === 'completed'
												? 'text-[var(--color-success)]'
												: t.status === 'in_progress'
													? 'text-[var(--color-accent)]'
													: 'text-[var(--color-text-dim)]'}"
										>
											{t.status === 'completed' ? '✓' : t.status === 'in_progress' ? '▶' : '○'}
										</span>
										<span
											class="min-w-0 break-words {t.status === 'completed'
												? 'text-[var(--color-text-dim)] line-through'
												: t.status === 'in_progress'
													? 'text-[var(--color-text)] font-semibold'
													: 'text-[var(--color-text-dim)]'}"
										>
											{t.status === 'in_progress' ? (t.activeForm ?? t.content) : t.content}
										</span>
									</li>
								{/each}
							</ul>
						</div>
					{/if}
				{:else if block.type === 'tool_use' && block.name === 'Bash' && bashOf(block.input)}
					{@const b = bashOf(block.input)}
					<details class="text-xs my-1.5">
						<summary
							class="cursor-pointer text-[var(--color-text-dim)] py-1 select-none flex items-center gap-1.5 hover:text-[var(--color-text)]"
						>
							<span class="opacity-60">⏺</span>
							<span class="font-bold">Bash</span>
							<span class="font-mono opacity-70 truncate min-w-0">{firstLine(b?.command ?? '')}</span>
						</summary>
						{#if b?.description}
							<div class="mt-1 text-[var(--color-text-dim)] italic">{b.description}</div>
						{/if}
						<pre
							class="mt-1 p-2.5 rounded-lg bg-[var(--color-bg)] border border-[var(--color-border)]/50 font-mono text-xs whitespace-pre-wrap break-words overflow-auto max-h-72">{b?.command ?? ''}</pre>
					</details>
				{:else if block.type === 'tool_use'}
					<details class="text-xs my-1.5">
						<summary
							class="cursor-pointer text-[var(--color-text-dim)] py-1 select-none flex items-center gap-1.5 hover:text-[var(--color-text)]"
						>
							<span class="opacity-60">🔧</span>
							<span class="font-medium">{block.name}</span>
							{#if toolSummary(block.input)}
								<span class="font-mono opacity-70 truncate min-w-0">{toolSummary(block.input)}</span>
							{/if}
						</summary>
						<pre
							class="mt-1 p-2.5 rounded-lg bg-[var(--color-bg)] border border-[var(--color-border)]/50 font-mono text-xs whitespace-pre-wrap break-all overflow-x-auto max-h-64">{JSON.stringify(block.input, null, 2)}</pre>
					</details>
				{/if}
			{/each}
		{/if}
	</div>
{:else}
	<div class="text-xs text-[var(--color-text-dim)] px-2 opacity-50">
		[{msg.kind}/{msg.role ?? '?'}]
	</div>
{/if}

<style>
	:global(.cb-md p) { margin: 0 0 0.6rem 0; }
	:global(.cb-md p:last-child) { margin-bottom: 0; }
	:global(.cb-md h1),
	:global(.cb-md h2),
	:global(.cb-md h3),
	:global(.cb-md h4) {
		font-weight: 600; margin: 0.8rem 0 0.4rem 0; line-height: 1.3;
	}
	:global(.cb-md h1) { font-size: 1.2rem; }
	:global(.cb-md h2) { font-size: 1.1rem; }
	:global(.cb-md h3) { font-size: 1rem; }
	:global(.cb-md ul),
	:global(.cb-md ol) { margin: 0.4rem 0 0.6rem 0; padding-left: 1.4rem; }
	:global(.cb-md li) { margin: 0.2rem 0; }
	:global(.cb-md code) {
		background: rgba(15, 23, 42, 0.7); padding: 0.1rem 0.4rem; border-radius: 4px;
		font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.88em;
	}
	:global(.cb-md pre) {
		background: rgba(15, 23, 42, 0.9); border: 1px solid rgba(71, 85, 105, 0.4);
		padding: 0.7rem 0.85rem; border-radius: 8px; overflow-x: auto;
		margin: 0.6rem 0; font-size: 0.85rem; line-height: 1.45;
	}
	:global(.cb-md pre code) { background: transparent; padding: 0; }
	:global(.cb-md a) { color: var(--color-accent); text-decoration: underline; }
	:global(.cb-md strong) { font-weight: 700; color: var(--color-text); }
	:global(.cb-md em) { font-style: italic; }
	:global(.cb-md blockquote) {
		border-left: 3px solid var(--color-border); margin: 0.6rem 0;
		padding-left: 0.85rem; color: var(--color-text-dim);
	}
	:global(.cb-md table) { border-collapse: collapse; margin: 0.6rem 0; font-size: 0.9rem; }
	:global(.cb-md th),
	:global(.cb-md td) { border: 1px solid var(--color-border); padding: 0.4rem 0.6rem; text-align: left; }
	:global(.cb-md th) { background: rgba(15, 23, 42, 0.5); font-weight: 600; }
	:global(.cb-md hr) { border: 0; border-top: 1px solid var(--color-border); margin: 0.8rem 0; }
	/* highlight.js — dark palette (PWA is dark by default), GitHub-ish, calm */
	:global(.cb-md .hljs-comment),
	:global(.cb-md .hljs-quote) { color: #8b949e; font-style: italic; }
	:global(.cb-md .hljs-keyword),
	:global(.cb-md .hljs-selector-tag),
	:global(.cb-md .hljs-built_in),
	:global(.cb-md .hljs-meta) { color: #ff7b72; }
	:global(.cb-md .hljs-string),
	:global(.cb-md .hljs-attr),
	:global(.cb-md .hljs-regexp) { color: #a5d6ff; }
	:global(.cb-md .hljs-title),
	:global(.cb-md .hljs-section),
	:global(.cb-md .hljs-title.function_) { color: #d2a8ff; }
	:global(.cb-md .hljs-number),
	:global(.cb-md .hljs-literal),
	:global(.cb-md .hljs-symbol) { color: #79c0ff; }
	:global(.cb-md .hljs-variable),
	:global(.cb-md .hljs-template-variable),
	:global(.cb-md .hljs-type) { color: #ffa657; }
	:global(.cb-md .hljs-addition) { color: #3fb950; background: #2ea04326; }
	:global(.cb-md .hljs-deletion) { color: #f85149; background: #f8514926; }
	@media (prefers-color-scheme: light) {
		:global(.cb-md .hljs-comment),
		:global(.cb-md .hljs-quote) { color: #6e7781; }
		:global(.cb-md .hljs-keyword),
		:global(.cb-md .hljs-selector-tag),
		:global(.cb-md .hljs-built_in),
		:global(.cb-md .hljs-meta) { color: #cf222e; }
		:global(.cb-md .hljs-string),
		:global(.cb-md .hljs-attr),
		:global(.cb-md .hljs-regexp) { color: #0a3069; }
		:global(.cb-md .hljs-title),
		:global(.cb-md .hljs-section),
		:global(.cb-md .hljs-title.function_) { color: #8250df; }
		:global(.cb-md .hljs-number),
		:global(.cb-md .hljs-literal),
		:global(.cb-md .hljs-symbol) { color: #0550ae; }
		:global(.cb-md .hljs-variable),
		:global(.cb-md .hljs-template-variable),
		:global(.cb-md .hljs-type) { color: #953800; }
		:global(.cb-md .hljs-addition) { color: #116329; }
		:global(.cb-md .hljs-deletion) { color: #82071e; }
	}
</style>
