// Svelte action: syntax-highlight <pre><code class="language-xxx"> blocks that
// marked produced inside a {@html} container. highlight.js is dynamically
// imported so it lands in its own async chunk, NOT the initial bundle.

let highlightFn: ((code: string, lang?: string) => string | null) | null = null;
let loading: Promise<void> | null = null;

async function loader() {
	if (highlightFn) return;
	if (!loading) {
		loading = import('./highlight-core').then((m) => {
			highlightFn = m.highlight;
		});
	}
	await loading;
}

export function highlightCodeBlocks(node: HTMLElement) {
	async function run() {
		const blocks = node.querySelectorAll<HTMLElement>('pre > code');
		if (blocks.length === 0) return;
		let needLoad = false;
		blocks.forEach((c) => {
			if (c.dataset.hl !== '1') needLoad = true;
		});
		if (!needLoad) return;
		await loader();
		if (!highlightFn) return;
		blocks.forEach((c) => {
			if (c.dataset.hl === '1') return;
			c.dataset.hl = '1';
			const cls = Array.from(c.classList).find((x) => x.startsWith('language-'));
			const lang = cls ? cls.slice('language-'.length) : '';
			const out = highlightFn!(c.textContent ?? '', lang);
			if (out != null) {
				c.innerHTML = out;
				c.classList.add('hljs');
			}
		});
	}

	void run();
	const obs = new MutationObserver(() => void run());
	obs.observe(node, { childList: true, subtree: true });
	return {
		destroy() {
			obs.disconnect();
		}
	};
}
