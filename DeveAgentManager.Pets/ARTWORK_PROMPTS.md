# Silkie artwork prompts

These prompts were recorded in the original standalone pet prototype and are preserved below. The reference was
an owner-supplied Silkie character sheet; the artwork was generated with the built-in image-generation tool.
The final chicken is named **Byte**, with species ID `silkie`.

The `young` prompt was an intermediate baby variant. The later `chick` redesign requested more distinct hatchling
proportions. For how these sheets became the current WebP assets and animated renderer, see the
[pet project README](README.md#how-we-used-ai). The prompt text describes the requested output; exported sheets
were subsequently inspected and processed, including their actual dimensions and cell spacing.

## adult

Use case: stylized-concept
Asset type: production sprite atlas for a cute animated virtual pet.
Input image: the attached Silkie sheet is the character identity and drawing-style reference. Preserve its exact appealing character design.
Create a new clean TRANSPARENT sprite atlas, landscape, ideally 2048 x 1536 pixels, precisely FOUR equal columns by THREE equal rows, no gutters, no labels, no visible grid. Every cell contains ONE complete full-body illustration of the SAME adult Silkie chicken. Keep every pose fully within its cell with at least 8% margin. Feet baseline at 90% of each cell; neutral top tuft at 10%. Center the body on the same anchor in every cell. Each adult has exactly the same dimensions/proportions, same feather markings, same face placement. No background, shadow, labels, lettering, panel outlines or checkerboard baked into the asset. Genuine alpha transparency.
Character: compact egg/pear-shaped ball of creamy ivory fluff with bold irregular dark chocolate speckles, very full tall swept feather crest, tiny charcoal gray feet peeking below, tiny charcoal diamond beak, large glossy dark-brown round eyes with white catchlights. Head blends into body with no neck seam, no separate head bubble, no human torso. Short fluffy wings. Organic curved pointed feather tufts all around the silhouette, not circular scallops. Expressive hand-inked dark outlines, pale warm shading inside feather folds, delicately drawn little peach-cream feather strokes. Exact charming polished hand-drawn game-sticker look from the reference. Not 3D, not photoreal, not flat minimalist geometric icon.
Exact cell order, left to right then down:
Row 1: (1) IDLE looking forward, eyes open, both wings relaxed; (2) BLINK identical idle body, eyes closed in gentle curved lines; (3) HAPPY eyes joyful, open smiling beak, wings spread halfway, a delighted small bounce pose; (4) WAVE right-side wing lifted high, left wing relaxed, eyes open, cheerful open beak.
Row 2: (5) SAD with drooping wings and glossy blue tears; (6) SHOCKED wide eyes, small open beak, both wings close to cheeks; (7) SLEEP eyes shut, body gently tucked lower, both wings folded, no letters or zzz; (8) PECK head leaning down, beak pointing toward ground, lower silhouette, wings relaxed.
Row 3: (9) WALK LEFT three-quarter turn facing left, one foot slightly lifted; (10) WALK RIGHT mirrored three-quarter turn facing right, one foot slightly lifted; (11) HOP both wings raised outward, both feet visible tucked slightly, excited open eyes; (12) CHEER eyes closed happy, big smile and both wings raised high.
Do not add sparkles, tears except SAD, or floating props. Each cell only the bird. Preserve the fluffy nuanced drawing from the reference: generous creamy feathers and darker bold speckles, a huge messy crest and round lovable face. The purpose is consistent replacement drawings for a rigged browser pet.

## young

Use case: stylized-concept
Asset type: transparent sprite atlas for an animated baby Silkie virtual pet.
Reference image is the completed ADULT Silkie expression atlas. Make the BABY CHICK version in the exact same drawing style, same ink lines, same face, same warm cream palette and dark beak/feet. This is a younger growth stage of the SAME character. It must remain unmistakably a Silkie: soft round pear-shaped ball of fluff, tiny plump wings, oversized glossy dark brown eyes with white highlights, small graphite diamond beak and tiny graphite feet. Much shorter three-to-five feather fluffy crest; baby-soft pale buttercream down (not saturated yellow); only a few little chocolate speckles concentrated on crest and wings, fewer than adult. Extremely cute compact chubby chick. Continuous fluffy head/body, no neck seam. Organic pointed feather curves, not circular cloud scallops. Lovely hand-drawn polished cartoon sticker art, not flat geometric vector art, not 3D.
Output one landscape sprite atlas, exact FOUR equal columns by THREE equal rows, ideally 2048x1536, entirely transparent genuine alpha. NO labels, NO grid, NO checkerboard, NO shadows, NO text, NO decorative objects. One complete full-body chick per cell, no clipping. 10% empty margin around each bird. Same feet baseline at 90% cell height, same body size/center, only pose/expression changes. Baby fills roughly 75% cell height.
Exact 12 cells in row-major order:
Row 1: 1 IDLE looking forward eyes open wings resting; 2 BLINK same idle pose eyes closed; 3 HAPPY joyful open eyes open smiling beak wings half-spread; 4 WAVE one fluffy wing raised high, cute open smile.
Row 2: 5 SAD downcast with blue tears and droopy wings; 6 SHOCKED wide eyes open beak wings near cheeks; 7 SLEEP eyes gently shut body tucked lower wings folded; 8 PECK leaning head down with beak towards ground, full body intact.
Row 3: 9 WALK LEFT three-quarter facing left one tiny foot up; 10 WALK RIGHT three-quarter facing right one tiny foot up; 11 HOP excited both wings spread feet tucked slightly; 12 CHEER happy closed eyes open smile both wings raised high.
No zzz, hearts, stars, props or extra characters. Maintain extremely consistent markings and body proportions across all cells.

## chick

Use case: stylized-concept
Asset type: sprite atlas, BABY CHICK redesign.
Use the supplied adult Silkie only as a drawing-style and species reference. The requested character has VERY DIFFERENT BABY PROPORTIONS: make a newly-hatched tiny round chick, NOT a shrunken adult. Completely replace the tall elaborate adult crest with just THREE SHORT soft down tufts. Its silhouette is a very squat fuzzy cream-yellow ball, almost as wide as it is tall, with disproportionately huge round brown eyes low on its face, tiny short beak and two tiny dark feet. The head and belly are a single round mass. Only THREE little dark feather flecks in total, no spotted adult coat. Its short pale butter-yellow baby down is smooth and soft, with little pointed wisps around the outer edge. Round, plump, sweet, fragile, irresistibly cute. The face uses the exact illustrated eye style from the adult: glossy brown/black with lovely white highlights. Warm ivory pale yellow feathers, NOT bright yellow.
Match the adult reference's rich hand-inked dark outlines and delicate cream feather shading. Full body. TRANSPARENT alpha backdrop, no shadow or checkerboard. No props, writing, labels or grid.
Create a perfectly regular FOUR columns by THREE rows sheet, ideally 2048x1536. Each cell same size. Each chick centered with 12% margins on all sides, feet at same 88% baseline. Same size and same character in every cell, consistent coat, face and SHORT three-tuft baby crest.
Row 1: neutral idle open eyes; blink gently closed eyes same body; happy open smile and spread tiny wings; one wing waving up.
Row 2: sad with small blue tears; shocked wide eyes tiny open beak wings at cheeks; sleeping tucked round ball eyes shut; pecking down toward ground.
Row 3: walking three-quarter left one foot up; walking three-quarter right one foot up; hopping with spread wings and lifted feet; cheering with happy shut eyes and both tiny wings up.
All twelve must be clearly NEWLY HATCHED BABY CHICKS, stubby three-feather tuft, huge baby eyes and very few markings. DO NOT repeat the adult tall many-layered crown.
