declare module "cloudflare:test" {
  export const SELF: Fetcher;
  export const env: { ROOM: DurableObjectNamespace };
  export function runDurableObjectAlarm(stub: DurableObjectStub): Promise<boolean>;
}
