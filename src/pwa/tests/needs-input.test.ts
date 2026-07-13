import { describe, it, expect } from 'vitest';
import { shouldReArmNeedsInput } from '../src/lib/utils/needs-input';

const GUARD = 3000;

describe('shouldReArmNeedsInput', () => {
	it('re-arms when REST says needsInput and we are idle past the guard', () => {
		expect(
			shouldReArmNeedsInput({
				current: false,
				restNeedsInput: true,
				msSinceReply: 10_000,
				guardMs: GUARD
			})
		).toBe(true);
	});

	it('does not re-arm when REST says no prompt', () => {
		expect(
			shouldReArmNeedsInput({
				current: false,
				restNeedsInput: false,
				msSinceReply: 10_000,
				guardMs: GUARD
			})
		).toBe(false);
	});

	it('does not re-arm when the banner is already showing (no-op, additive only)', () => {
		expect(
			shouldReArmNeedsInput({
				current: true,
				restNeedsInput: true,
				msSinceReply: 10_000,
				guardMs: GUARD
			})
		).toBe(false);
	});

	it('does not re-flip within the stale-reply guard window', () => {
		// User just replied 500ms ago (optimistic clear); a lagging REST read
		// still reporting the just-answered prompt must not undo the clear.
		expect(
			shouldReArmNeedsInput({
				current: false,
				restNeedsInput: true,
				msSinceReply: 500,
				guardMs: GUARD
			})
		).toBe(false);
	});

	it('re-arms exactly at the guard boundary', () => {
		expect(
			shouldReArmNeedsInput({
				current: false,
				restNeedsInput: true,
				msSinceReply: GUARD,
				guardMs: GUARD
			})
		).toBe(true);
	});
});
