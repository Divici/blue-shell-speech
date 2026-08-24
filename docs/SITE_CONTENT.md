# Public Site Content — Confirmed

Source of truth for public-site copy. Confirmed with Michelle 2026-08-23.

**Placeholders are marked `PLACEHOLDER`.** They must not ship. Real contact details come from
environment config, never from this file and never from the tree — this repo is public.

---

## Identity

| Field | Value |
|---|---|
| Practice | Blue Shell Speech |
| Clinician | Michelle |
| Role | Licensed Speech-Language Pathologist |
| Population | Birth to 5 years |
| Service area | **Maryland** |
| Delivery | In-home |
| Phone | `PLACEHOLDER` — env `NEXT_PUBLIC_PRACTICE_PHONE` |
| Email | `PLACEHOLDER` — env `NEXT_PUBLIC_PRACTICE_EMAIL` |

Michelle's home address never enters the tree, env config, or any rendered page. In-home
therapy travels to the patient; the practice does not publish a street address.

---

## Hero

- **Eyebrow:** COMMUNICATION OPENS DOORS
- **H1:** Helping Little Voices Make Big Connections
- **Sub:** Personalized speech-language therapy for children birth to 5 years. In-home care
  that supports growth, confidence, and everyday communication.
- **Primary CTA:** Request a Free Consultation → `/consultation`
- **Secondary CTA:** Learn More → anchor to Meet Your SLP

## Three badges

These state **how** Michelle works, not **what** she treats. That distinction is why the
services chips exist — see below.

| Badge | Caption |
|---|---|
| In-Home Therapy | Convenient & Comfortable |
| Birth to 5 Years | Early Support Matters |
| Personalized Care | Tailored to Your Child |

## Meet Your SLP

**Eyebrow:** ABOUT YOUR SLP · **Heading:** Meet Your SLP

> Hi, I'm Michelle! I'm a licensed Speech-Language Pathologist passionate about helping young
> children find their voice. I believe every child has the ability to communicate, connect,
> and thrive with the right support.

**Credentials — confirmed accurate, do not embellish:**

- Licensed SLP with specialized early childhood training
- Experience working with children birth to 5
- Family-centered, play-based approach
- Committed to your child's progress

**Service chips** (single light row — replaces the removed services grid):

`Speech & Language Therapy` · `Social Communication` · `Early Intervention (0–3)` ·
`AAC` · `In-Home Therapy`

**AAC is confirmed and required.** The cut services grid took it with it; the chips put it
back. Do not drop it in a redesign.

## Getting Started is Easy

1. **Request Consultation** — Reach out to schedule your free consultation.
2. **We Connect** — We'll learn about your child and your goals.
3. **Personalized Plan** — We create a therapy plan tailored to your child.
4. **Start Therapy** — Therapy begins in your home, where your child feels most comfortable.

## Get In Touch

Heading copy per Michelle: *Let's support your child's communication journey.*

## Removed — do not reintroduce

- **Services grid** ("Therapy That's Tailored to Your Child") — replaced by the chips.
- **Testimonials** ("Real Results. Happy Families.") — **deleted, not deferred.** They were
  fabricated. Placeholder reviews for a healthcare practice are a real problem, not a
  placeholder problem.
- **Resources nav tab** — no handouts exist. Build the resource system anyway so adding one
  later is a content change, not a feature.

---

## Navigation

| Item | Behaviour |
|---|---|
| Home / About / Services / Contact | Anchor-scroll on the homepage |
| Free Consultation | `/consultation` — real intake form, own route |
| Login | `/login` — styled **secondary** so parents are not drawn to it |

---

## Design tokens (comp 2 sidebar)

Blues `#2D7FF9` `#1B4FA3` `#E8F3FF` `#F5FAFF` · Teal `#6FC7C3` ·
Warm `#FFD786` `#FF8FA3` `#FFBD59` · Dark `#AA5568` · White `#FFFFFF`

Type: **Playfair Display** headings, **Inter** body.

**Two known deviations, both deliberate:**

1. Light-gray body copy in the comps fails 4.5:1. Darken it. Accessibility beats fidelity.
2. The comp labels its dark swatch `#AA5568` but renders something near-charcoal. The label is
   unreliable — sample the actual pixels before committing the token, and record which won.

---

## Assets

| Asset | State |
|---|---|
| `assets/headshot.PNG` | **Real headshot received 2026-08-23.** 2.4 MB PNG — convert to AVIF/WebP with responsive sizes before shipping. Blue studio backdrop sits close to `#E8F3FF`, so the organic blob mask works without a cutout. |
| `assets/children.png` | 2.1 MB — convert before shipping. |
| `coral-shell.svg` `sea-plant.svg` `starfish.svg` | Present. |
| Blue shell logo, wave dividers, blob masks, bubbles, icon set | **Missing — generate as optimized SVG.** |
