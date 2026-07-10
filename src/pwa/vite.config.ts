import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
	plugins: [
		tailwindcss(),
		sveltekit(),
		VitePWA({
			registerType: 'autoUpdate',
			// Use our hand-written src/sw.ts so push/notification handlers ship.
			// SvelteKit would auto-register src/service-worker.ts and conflict —
			// we renamed it to src/sw.ts so SvelteKit ignores it.
			strategies: 'injectManifest',
			srcDir: 'src',
			filename: 'sw.ts',
			injectRegister: 'script',
			injectManifest: {
				globPatterns: ['**/*.{js,css,html,svg,png,webmanifest}']
			},
			manifest: {
				name: 'CortexBridge',
				short_name: 'Cortex',
				description: 'Mobile control for Claude Code sessions',
				theme_color: '#0f172a',
				background_color: '#0f172a',
				display: 'standalone',
				orientation: 'portrait',
				start_url: '/',
				icons: [
					{ src: '/icon-192.png', sizes: '192x192', type: 'image/png' },
					{ src: '/icon-512.png', sizes: '512x512', type: 'image/png' }
				]
			}
		})
	],
	server: {
		proxy: {
			'/api': { target: 'http://localhost:3000', changeOrigin: true, ws: false },
			'/internal': { target: 'http://localhost:3000', changeOrigin: true }
		}
	},
	build: {
		target: 'es2022',
		sourcemap: false
	}
});
