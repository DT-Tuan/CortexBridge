// Svelte action: pull-to-refresh on a scroll container. Triggers `onRefresh`
// when the user drags down past a threshold while already scrolled to the top.
// Touch-only (mobile); pointer/mouse is ignored so desktop scroll is unaffected.

interface Opts {
	onRefresh: () => Promise<void> | void;
	threshold?: number;
}

export function pullToRefresh(node: HTMLElement, opts: Opts) {
	let startY = 0;
	let pulling = false;
	let armed = false;
	let busy = false;
	const threshold = opts.threshold ?? 64;

	const indicator = document.createElement('div');
	indicator.setAttribute('aria-hidden', 'true');
	indicator.style.cssText =
		'position:absolute;left:0;right:0;top:0;display:flex;align-items:center;justify-content:center;' +
		'height:0;overflow:hidden;color:var(--color-text-dim);font-size:13px;transition:height .15s;pointer-events:none;z-index:5;';
	indicator.textContent = '↓ Kéo để làm mới';
	const host = node.parentElement ?? node;
	if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
	host.insertBefore(indicator, host.firstChild);

	function onStart(e: TouchEvent) {
		if (busy || node.scrollTop > 0) return;
		startY = e.touches[0].clientY;
		pulling = true;
		armed = false;
	}
	function onMove(e: TouchEvent) {
		if (!pulling) return;
		const dy = e.touches[0].clientY - startY;
		if (dy <= 0 || node.scrollTop > 0) {
			indicator.style.height = '0';
			armed = false;
			return;
		}
		const pull = Math.min(dy * 0.5, threshold + 16);
		indicator.style.height = `${pull}px`;
		armed = pull >= threshold;
		indicator.textContent = armed ? '↑ Thả để làm mới' : '↓ Kéo để làm mới';
	}
	async function onEnd() {
		if (!pulling) return;
		pulling = false;
		if (armed && !busy) {
			busy = true;
			indicator.style.height = `${threshold}px`;
			indicator.textContent = 'Đang làm mới…';
			try {
				await opts.onRefresh();
			} finally {
				busy = false;
				indicator.style.height = '0';
			}
		} else {
			indicator.style.height = '0';
		}
		armed = false;
	}

	node.addEventListener('touchstart', onStart, { passive: true });
	node.addEventListener('touchmove', onMove, { passive: true });
	node.addEventListener('touchend', onEnd);
	node.addEventListener('touchcancel', onEnd);

	return {
		destroy() {
			node.removeEventListener('touchstart', onStart);
			node.removeEventListener('touchmove', onMove);
			node.removeEventListener('touchend', onEnd);
			node.removeEventListener('touchcancel', onEnd);
			indicator.remove();
		}
	};
}
