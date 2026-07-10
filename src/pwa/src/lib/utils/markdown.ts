import { marked } from 'marked';

// Configure marked: GFM (tables, fenced code), no header IDs, no email mangling.
// Sanitization: marked escapes raw HTML by default since v9; assistant output is
// trusted (server-side already redacted), but we still rely on this default.
marked.setOptions({
	gfm: true,
	breaks: true
});

/**
 * Render markdown to HTML for chat display. Assistant messages from CC are
 * trusted content (we control the source — Claude API). User-typed messages
 * are NOT passed through this function: they're displayed as-is in pre-wrap.
 */
export function renderMarkdown(src: string): string {
	if (!src) return '';
	try {
		return marked.parse(src) as string;
	} catch {
		// Fall back to escaped plain text if marked throws on malformed input
		return escapeHtml(src);
	}
}

function escapeHtml(s: string): string {
	return s
		.replace(/&/g, '&amp;')
		.replace(/</g, '&lt;')
		.replace(/>/g, '&gt;')
		.replace(/"/g, '&quot;')
		.replace(/'/g, '&#39;');
}
