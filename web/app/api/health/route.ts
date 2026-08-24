import { NextResponse } from "next/server";

/**
 * Liveness probe for Container Apps and the Docker healthcheck.
 *
 * Deliberately does NOT check the API or the database. `web` serving the public marketing
 * site is useful while `api` is still cold or down, and a health check that fails because a
 * dependency is asleep would restart a container that is working correctly.
 *
 * Readiness of downstream services is checked by the API's own /health/ready.
 */
export const dynamic = "force-dynamic";

export function GET() {
  return NextResponse.json(
    { status: "ok", service: "web" },
    { headers: { "Cache-Control": "no-store" } },
  );
}
