/**
 * Public-site copy, confirmed with Michelle 2026-08-23.
 *
 * Mirrors docs/SITE_CONTENT.md, which is the record of what was agreed. Kept as data
 * rather than inlined into JSX so the wording can be reviewed in one place — and so a
 * test can assert the confirmed content is actually what renders.
 *
 * The credentials are accurate as written. DO NOT embellish them: this is a licensed
 * clinician's public professional description, and an invented credential is a
 * licensing problem, not a copy problem.
 */

export const NAV_ITEMS = [
  { label: "Home", href: "#top" },
  { label: "About", href: "#about" },
  { label: "Services", href: "#services" },
  { label: "Contact", href: "#contact" },
] as const;

export const HERO = {
  eyebrow: "Communication opens doors",
  heading: ["Helping Little Voices", "Make Big Connections"],
  body:
    "Personalized speech-language therapy for children birth to 5 years. " +
    "In-home care that supports growth, confidence, and everyday communication.",
  primaryCta: { label: "Request a Free Consultation", href: "/consultation" },
  secondaryCta: { label: "Learn More", href: "#about" },
} as const;

/**
 * These describe HOW Michelle works, not WHAT she treats — which is exactly why the
 * service chips exist. Removing the services grid took the only mention of AAC with it.
 */
export const BADGES = [
  { title: "In-Home Therapy", caption: "Convenient & Comfortable", icon: "home" },
  { title: "Birth to 5 Years", caption: "Early Support Matters", icon: "heart" },
  { title: "Personalized Care", caption: "Tailored to Your Child", icon: "star" },
] as const;

export const ABOUT = {
  eyebrow: "About your SLP",
  heading: "Meet Your SLP",
  body:
    "Hi, I'm Michelle! I'm a licensed Speech-Language Pathologist passionate about " +
    "helping young children find their voice. I believe every child has the ability to " +
    "communicate, connect, and thrive with the right support.",
  credentials: [
    "Licensed SLP with specialized early childhood training",
    "Experience working with children birth to 5",
    "Family-centered, play-based approach",
    "Committed to your child's progress",
  ],
} as const;

/**
 * AAC is confirmed and required. It replaces the mention lost when the services grid
 * was removed, and it is the term parents searching for an AAC provider actually use.
 */
export const SERVICE_CHIPS = [
  { label: "Speech & Language Therapy", icon: "chat" },
  { label: "Social Communication", icon: "people" },
  { label: "Early Intervention (0–3)", icon: "hand-heart" },
  { label: "AAC", icon: "aac" },
  { label: "In-Home Therapy", icon: "home" },
] as const;

export const STEPS = [
  {
    number: 1,
    title: "Request Consultation",
    body: "Reach out to schedule your free consultation.",
    icon: "calendar",
  },
  {
    number: 2,
    title: "We Connect",
    body: "We'll learn about your child and your goals.",
    icon: "chat",
  },
  {
    number: 3,
    title: "Personalized Plan",
    body: "We create a therapy plan tailored to your child.",
    icon: "heart",
  },
  {
    number: 4,
    title: "Start Therapy",
    body: "Therapy begins in your home, where your child feels most comfortable.",
    icon: "star",
  },
] as const;

export const CONTACT = {
  eyebrow: "Get in touch",
  heading: "Let's support your child's communication journey.",
  body:
    "Have a question, or wondering whether therapy is the right fit? " +
    "Reach out and we'll talk it through — no cost, no pressure.",
  serviceArea: "Maryland",
} as const;

export const PRACTICE_NAME = "Blue Shell Speech";
