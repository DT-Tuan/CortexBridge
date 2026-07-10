// Best-effort tactile feedback on send.
//
// - Android/Chrome: Vibration API works.
// - iOS Safari: no Vibration API. The community-known workaround is briefly
//   toggling a `<input type="checkbox" switch>` (iOS 17.4+) which plays the
//   native switch haptic. It's a hack and silently no-ops on older iOS, so we
//   treat it as a bonus, not a guarantee.

let switchEl: HTMLInputElement | null = null;

function iosSwitchTick() {
	try {
		if (!switchEl) {
			switchEl = document.createElement('input');
			switchEl.type = 'checkbox';
			// `switch` is the iOS-native toggle; the attribute is ignored elsewhere.
			switchEl.setAttribute('switch', '');
			switchEl.setAttribute('aria-hidden', 'true');
			switchEl.tabIndex = -1;
			switchEl.style.cssText =
				'position:fixed;left:-9999px;top:-9999px;width:1px;height:1px;opacity:0;pointer-events:none;';
			document.body.appendChild(switchEl);
		}
		// Toggling fires the system switch haptic on supported iOS.
		switchEl.checked = !switchEl.checked;
		switchEl.dispatchEvent(new Event('change'));
	} catch {
		/* ignore — haptic is non-essential */
	}
}

export function hapticTick() {
	const nav = navigator as Navigator & { vibrate?: (p: number | number[]) => boolean };
	if (typeof nav.vibrate === 'function') {
		try {
			if (nav.vibrate(8)) return;
		} catch {
			/* fall through to iOS path */
		}
	}
	iosSwitchTick();
}
