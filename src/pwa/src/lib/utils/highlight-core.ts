// Heavy module — ONLY import this dynamically (see code-highlight.ts) so
// highlight.js stays out of the PWA initial bundle (≤ 50 KB gz budget).

import hljs from 'highlight.js/lib/core';
import bash from 'highlight.js/lib/languages/bash';
import shell from 'highlight.js/lib/languages/shell';
import javascript from 'highlight.js/lib/languages/javascript';
import typescript from 'highlight.js/lib/languages/typescript';
import json from 'highlight.js/lib/languages/json';
import python from 'highlight.js/lib/languages/python';
import csharp from 'highlight.js/lib/languages/csharp';
import css from 'highlight.js/lib/languages/css';
import xml from 'highlight.js/lib/languages/xml';
import markdown from 'highlight.js/lib/languages/markdown';
import yaml from 'highlight.js/lib/languages/yaml';
import sql from 'highlight.js/lib/languages/sql';
import diff from 'highlight.js/lib/languages/diff';
import go from 'highlight.js/lib/languages/go';
import rust from 'highlight.js/lib/languages/rust';
import dockerfile from 'highlight.js/lib/languages/dockerfile';
import ini from 'highlight.js/lib/languages/ini';

const map: Record<string, unknown> = {
	bash,
	sh: shell,
	shell,
	javascript,
	js: javascript,
	typescript,
	ts: typescript,
	json,
	python,
	py: python,
	csharp,
	cs: csharp,
	css,
	xml,
	html: xml,
	svelte: xml,
	markdown,
	md: markdown,
	yaml,
	yml: yaml,
	sql,
	diff,
	go,
	rust,
	rs: rust,
	dockerfile,
	ini,
	toml: ini
};

let registered = false;
function ensure() {
	if (registered) return;
	registered = true;
	for (const [name, lang] of Object.entries(map)) {
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		hljs.registerLanguage(name, lang as any);
	}
}

export function highlight(code: string, lang?: string): string | null {
	ensure();
	const l = (lang || '').toLowerCase().trim();
	if (!l || !hljs.getLanguage(l)) return null;
	try {
		return hljs.highlight(code, { language: l, ignoreIllegals: true }).value;
	} catch {
		return null;
	}
}
