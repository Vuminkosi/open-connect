import { defineCollection } from 'astro:content';
import { docsSchema } from '@astrojs/starlight/schema';

export const collections = {
	// Starlight 0.29 (Astro 4) requires the docs collection schema to be
	// registered explicitly. docsSchema() defaults `draft: false`, which is
	// what keeps production builds from filtering out every page.
	docs: defineCollection({ schema: docsSchema() }),
};
