# Blue Shell Speech --- Frontend Engineering Rules

These rules are based on the senior frontend interview transcript and
adapted into practical requirements for Blue Shell Speech. The central
principle is that a production frontend should be evaluated not only by
whether it works, but by its **performance, security, accessibility,
testing, maintainability, and architectural tradeoffs**.

## 1. Images and Static Assets

-   Never ship an image substantially larger than its rendered
    dimensions.
-   Prefer **AVIF/WebP** for photographs and **SVG** for logos, icons,
    waves, shells, and illustrations.
-   Use Next.js `<Image>` whenever appropriate so responsive sizing and
    optimization happen automatically.
-   Always provide explicit dimensions or aspect ratios to prevent
    **Cumulative Layout Shift (CLS)**.
-   Hero/LCP imagery should load immediately; below-the-fold images
    should lazy-load.
-   Provide appropriate responsive `sizes`.
-   Decorative images should use `alt=""`; meaningful images need useful
    alt text.
-   Keep decorative SVGs lightweight and reusable rather than converting
    them into PNGs.
-   The Blue Shell Speech SVG assets are appropriate as SVGs.
-   The JPG photographs should ideally be delivered through Next.js
    image optimization so browsers can receive appropriately sized
    WebP/AVIF versions.

## 2. JavaScript and Rendering Performance

-   Default to **React Server Components** in Next.js.
-   Add `"use client"` only when actual client-side interactivity
    requires it.
-   Do not ship JavaScript for static presentation.
-   Dynamically import genuinely heavy client functionality when it is
    not required for initial rendering.
-   Avoid unnecessary dependencies.
-   Keep state as close as possible to where it is consumed.
-   Do not introduce Context or global state unless multiple distant
    components actually require it.
-   Memoize based on measured need rather than automatically.
-   Periodically inspect bundle size.
-   Understand what Next.js handles automatically: bundling,
    minification, code splitting, tree shaking, and production
    optimization. Do not manually recreate this infrastructure without a
    reason.

## 3. Security

This is especially important because the eventual authenticated
application may contain PHI.

-   Never trust browser input.
-   Validate data again on the server even when client-side validation
    exists.
-   Never store PHI, passwords, access tokens, session secrets, or
    sensitive patient information in `localStorage` or `sessionStorage`.
-   Prefer secure server-managed sessions using `HttpOnly`, `Secure`,
    and appropriate `SameSite` cookies.
-   Never expose secrets through `NEXT_PUBLIC_*`.
-   Avoid `dangerouslySetInnerHTML`.
-   If rendering user-generated rich content becomes necessary, sanitize
    it with a proven library.
-   Rely on React's default escaping for ordinary rendered content.
-   Implement authorization **server-side**, not merely by hiding
    frontend UI.
-   Protect state-changing operations appropriately against CSRF.
-   Add appropriate security headers and a Content Security Policy where
    practical.
-   Keep dependencies updated and perform dependency/security scanning.
-   Treat all data crossing trust boundaries as untrusted.

### XSS Rule

Never directly render arbitrary HTML or JavaScript received from users
or backend data. Sanitize untrusted rich content and avoid unsafe
rendering APIs unless there is a specific, reviewed requirement.

## 4. Accessibility

Every feature should be usable without a mouse.

-   Use semantic HTML first.
-   Maintain a correct heading hierarchy.
-   Use `<button>` for actions and `<a>` for navigation.
-   Give every form input a proper label.
-   Provide visible keyboard focus states.
-   Maintain sufficient color contrast.
-   Make menus, dialogs, forms, and other controls keyboard-operable.
-   Use ARIA only when native HTML cannot adequately express the
    behavior.
-   Provide appropriate screen-reader announcements for asynchronous
    operations when necessary.
-   Give meaningful images descriptive alt text and decorative images
    empty alt text.
-   Test important flows with keyboard navigation and a screen reader.
-   Include automated accessibility checks in the quality pipeline.
-   Respect `prefers-reduced-motion`.

Example:

``` css
@media (prefers-reduced-motion: reduce) {
  *,
  *::before,
  *::after {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
    scroll-behavior: auto !important;
  }
}
```

## 5. Motion and Animation

Animation should communicate structure rather than exist only for
decoration.

-   Animate primarily **on scroll** for the public Blue Shell Speech
    site.
-   Favor `transform` and `opacity`.
-   Avoid animating layout properties such as `width`, `height`, `top`,
    and `left` where possible.
-   Keep transitions short and subtle.
-   Do not delay user interaction for animation.
-   Do not animate every element simply because it is possible.
-   Respect reduced-motion preferences.
-   Decorative parallax should be subtle and should never interfere with
    readability or interaction.
-   Motion should reinforce the flowing ocean-inspired design without
    making the healthcare site feel distracting or unprofessional.

## 6. Core Web Vitals and Performance Targets

Treat performance as an acceptance criterion rather than an
afterthought.

Target:

-   **LCP:** ≤ 2.5 seconds
-   **INP:** ≤ 200 ms
-   **CLS:** ≤ 0.1

Run Lighthouse before considering a public page finished.

Suggested Lighthouse targets:

-   Performance: **90+**
-   Accessibility: **95+**
-   Best Practices: **95+**
-   SEO: **95+**

Monitor performance over time so additions such as images, fonts,
scripts, or dependencies do not silently create regressions.

## 7. Code Quality

Use a consistent automated quality pipeline.

Recommended baseline:

``` text
TypeScript strict mode
ESLint
Prettier
Unit/component tests
Integration tests
Critical E2E tests
Accessibility checks
Dependency scanning
Lighthouse/performance checks
```

CI should reject important lint, type, test, security, accessibility, or
build failures.

Code quality tooling should reduce unnecessary stylistic debate and
catch problems before review or deployment.

## 8. Component Architecture

Do not make everything a component merely for abstraction.

Extract a component when:

-   It is reused.
-   It has independent behavior.
-   It significantly simplifies its parent.
-   It represents a meaningful UI or domain concept.

Prefer meaningful domain components such as:

``` text
ConsultationForm
ServiceCard
PatientCard
SessionRecorder
SoapNoteEditor
ScheduleCalendar
```

Avoid arbitrary abstractions such as:

``` text
BlueBox
GenericContainer
Wrapper2
ContentThing
```

Additional rules:

-   Keep domain logic separate from presentation where practical.
-   Keep state near the components that own it.
-   Avoid excessive prop drilling, but do not introduce global state
    simply to avoid one or two levels of props.
-   Prefer composition over overly configurable generic components.
-   Make component APIs explicit and strongly typed.

## 9. Fonts

-   Prefer `next/font`.
-   Load only font weights actually used.
-   Avoid excessive font families.
-   Prevent font loading from unnecessarily blocking rendering.
-   Use appropriate fallback fonts.
-   Keep typography consistent through reusable design tokens/styles.

## 10. CDN and Static Delivery

Static assets should be cached and delivered efficiently.

-   Use the CDN/edge delivery capabilities of the selected hosting
    platform.
-   Apply appropriate caching policies to immutable static assets.
-   Use content-hashed build assets where supported.
-   Do not unnecessarily send static assets through application servers.
-   Ensure asset delivery works geographically without introducing
    unnecessary infrastructure.

For a Next.js deployment, prefer using the hosting platform's existing
CDN/image pipeline before introducing additional infrastructure.

## 11. Browser Storage

Choose browser storage according to the lifetime and sensitivity of the
information.

### Cookies

Useful when information must participate in server requests, especially
secure session management. Sensitive session cookies should generally be
`HttpOnly`, `Secure`, and configured with an appropriate `SameSite`
policy.

### localStorage

Suitable only for non-sensitive client preferences that should persist
across browser sessions, such as harmless UI preferences.

Do **not** use it for PHI, authentication secrets, or other sensitive
information.

### sessionStorage

Suitable for non-sensitive temporary browser-tab state that should
disappear when the session/tab ends.

Do **not** assume browser storage is a secure location for sensitive
healthcare information.

## 12. Testing Strategy

Spend meaningful engineering time testing and optimizing the
application, not only building features.

Testing should include:

-   Unit tests for important isolated logic.
-   Component tests for important UI behavior.
-   Integration tests across frontend/backend boundaries.
-   E2E tests for critical user journeys.
-   Accessibility testing.
-   Performance testing.
-   Security/dependency scanning.
-   Manual testing of important responsive layouts.
-   Browser developer tools for network, performance, memory, and
    rendering investigation.

Test behavior rather than implementation details wherever practical.

## 13. Source Maps and Production Debugging

Production code will be minified and transformed.

-   Preserve useful source maps for controlled production
    debugging/error monitoring.
-   Do not publicly expose sensitive source information unnecessarily.
-   Use production error monitoring when appropriate.
-   Make production errors traceable back to the original
    TypeScript/React source.

## 14. Avoid Premature Architectural Complexity

Choose the simplest architecture that satisfies today's requirements
while leaving reasonable paths for tomorrow's requirements.

Do not introduce technology merely to demonstrate familiarity with it.

Examples of things Blue Shell Speech should **not** adopt without a
demonstrated need:

-   Micro-frontends
-   Excessive microservices
-   Global state libraries for small/local state
-   Complex event infrastructure
-   Custom bundling infrastructure already handled by Next.js
-   Additional caching layers without measured need
-   Large dependencies for trivial functionality

### Micro-frontends

Micro-frontends are primarily useful when organizational scale requires
multiple frontend teams to develop and deploy portions of a product
independently.

They introduce significant costs:

-   More complicated tooling
-   Harder shared-state management
-   Cross-application consistency problems
-   More complex deployments
-   More coordination
-   Additional runtime and operational complexity

Blue Shell Speech does not currently justify that tradeoff.

## 15. Senior-Engineer Decision Rule

For every meaningful technical decision, be prepared to answer:

1.  What problem are we solving?
2.  Why did we choose this approach?
3.  What alternatives did we consider?
4.  What tradeoffs does this introduce?
5.  How does it affect performance?
6.  How does it affect accessibility?
7.  How does it affect security?
8.  How will it be tested?
9.  How will it behave as the application grows?
10. Is this complexity actually justified today?

The goal is not to use the most technology. The goal is to make
deliberate engineering decisions and understand their consequences.

------------------------------------------------------------------------

# Claude Code Project Rule

> **Treat Blue Shell Speech as a production application, not a
> prototype. Every implementation decision should consider security,
> accessibility, performance, maintainability, and testability. Default
> to React Server Components and semantic HTML; minimize client
> JavaScript; keep state local unless sharing is justified; optimize all
> images and fonts; prevent layout shifts; respect reduced-motion
> preferences; never expose secrets or sensitive healthcare information
> to browser storage; validate and authorize sensitive operations
> server-side; avoid unsafe HTML rendering; maintain strict TypeScript,
> linting, tests, accessibility checks, dependency scanning, and
> Lighthouse checks. Target Core Web Vitals of LCP ≤2.5s, INP ≤200ms,
> and CLS ≤0.1. Prefer simple, explicit architecture over premature
> abstractions or unnecessary dependencies. Any deviation from these
> rules should have a documented technical reason.**

## Interview Demonstration Principle

Do not merely state these rules during an interview. Use Blue Shell
Speech to demonstrate them.

When walking through the application:

-   Point to a concrete implementation.
-   Explain the problem it solves.
-   Explain why that implementation was chosen.
-   Discuss an alternative.
-   Explain the tradeoff.
-   Mention how it was tested or measured.

That turns the application itself into evidence of senior-level frontend
engineering judgment.
