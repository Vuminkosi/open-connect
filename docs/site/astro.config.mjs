import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// Static-only docs frontend (SITE-001, Phases 1-2).
// No backend, no provider credentials, no VoiceDesk.
// Search: Starlight's built-in Pagefind (offline, generated at build time).
export default defineConfig({
	site: 'https://openconnect.dev',
	base: '/docs/',
	trailingSlash: 'always',
	integrations: [
		starlight({
			title: 'OpenConnect',
			description:
				'An open-source, .NET-first multimodal AI application stack built on Microsoft Agent Framework, with secure model access, durable conversations, observable agents, and reusable gateway infrastructure.',
			logo: {
				src: './src/assets/logo.svg',
				replacesTitle: false,
			},
			favicon: '/favicon.svg',
			editLink: {
				baseUrl: 'https://github.com/openconnect/open-connect/edit/main/docs/site/',
			},
			tableOfContents: { minHeadingLevel: 2, maxHeadingLevel: 3 },
			lastUpdated: false,
			customCss: ['./src/styles/custom.css'],
			components: {
				// Minimal stubs; full branded overrides land in a later phase.
			},
			sidebar: [
				{
					label: 'Introduction',
					items: [
						{ label: 'What is OpenConnect?', slug: 'introduction/what-is-openconnect' },
						{ label: 'Features', slug: 'introduction/features' },
						{ label: 'Architecture Overview', slug: 'introduction/architecture-overview' },
						{ label: 'Project Roadmap', slug: 'introduction/roadmap' },
					],
				},
				{
					label: 'Getting Started',
					items: [
						{ label: 'Prerequisites', slug: 'getting-started/prerequisites' },
						{ label: 'Installation', slug: 'getting-started/installation' },
						{ label: 'First Application', slug: 'getting-started/first-application' },
						{ label: 'Configuration Basics', slug: 'getting-started/configuration-basics' },
					],
				},
				{
					label: 'Architecture',
					items: [
						{ label: 'System Architecture', slug: 'architecture/system-architecture' },
						{ label: 'Request Lifecycle', slug: 'architecture/request-lifecycle' },
						{ label: 'Dependency Injection', slug: 'architecture/dependency-injection' },
					],
				},
				{
					label: 'AI Firewall',
					items: [
						{ label: 'Overview', slug: 'firewall/overview' },
						{ label: 'Detector Configuration', slug: 'firewall/detector-configuration' },
						{ label: 'Policies', slug: 'firewall/policies' },
					],
				},
				{
					label: 'Agents',
					items: [
						{ label: 'Overview', slug: 'agents/overview' },
						{ label: 'Interactive Agents', slug: 'agents/interactive-agents' },
						{ label: 'Agent Lifecycle', slug: 'agents/agent-lifecycle' },
					],
				},
				{
					label: 'API Reference',
					items: [
						{ label: '.NET API (generated)', slug: 'api-reference/dotnet' },
						{ label: 'REST API (generated)', slug: 'api-reference/rest' },
						{ label: 'Integration Contracts', slug: 'api-reference/contracts' },
					],
				},
			],
		}),
	],
});
