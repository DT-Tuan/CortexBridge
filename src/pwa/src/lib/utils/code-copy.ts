// Svelte action: decorate <pre><code> blocks produced by renderMarkdown() with
// a hover "Copy" button. Parity with the companion extension's CodeBlock.
// Attach to the container that holds {@html renderMarkdown(...)} output.

export function copyableCodeBlocks(node: HTMLElement) {
	function decorate() {
		const pres = node.querySelectorAll<HTMLPreElement>('pre');
		pres.forEach((pre) => {
			if (pre.dataset.cbCopy === '1') return;
			pre.dataset.cbCopy = '1';
			pre.style.position = 'relative';

			const btn = document.createElement('button');
			btn.type = 'button';
			btn.textContent = 'Copy';
			btn.setAttribute('aria-label', 'Copy code');
			btn.className =
				'cb-copy-btn absolute top-1.5 right-1.5 text-[11px] px-2 py-0.5 rounded ' +
				'border border-[var(--color-border)] bg-[var(--color-surface)] ' +
				'text-[var(--color-text-dim)] opacity-0 transition-opacity';

			btn.addEventListener('click', async (e) => {
				e.stopPropagation();
				const code = pre.querySelector('code')?.textContent ?? pre.textContent ?? '';
				try {
					await navigator.clipboard.writeText(code);
				} catch {
					const ta = document.createElement('textarea');
					ta.value = code;
					ta.style.position = 'fixed';
					ta.style.opacity = '0';
					document.body.appendChild(ta);
					ta.select();
					try {
						document.execCommand('copy');
					} catch {
						/* ignore */
					}
					document.body.removeChild(ta);
				}
				btn.textContent = 'Copied';
				setTimeout(() => (btn.textContent = 'Copy'), 1500);
			});

			pre.addEventListener('mouseenter', () => (btn.style.opacity = '1'));
			pre.addEventListener('mouseleave', () => (btn.style.opacity = '0'));
			pre.appendChild(btn);
		});
	}

	decorate();
	const obs = new MutationObserver(() => decorate());
	obs.observe(node, { childList: true, subtree: true });

	return {
		destroy() {
			obs.disconnect();
		}
	};
}
