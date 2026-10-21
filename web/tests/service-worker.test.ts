import { describe, expect, it } from 'vitest';
import config from '../ngsw-config.json';

describe('service-worker data boundaries',()=>{
  it('never caches authenticated APIs or navigation fallbacks under API routes',()=>{expect(config.dataGroups).toEqual([]);expect(config.navigationUrls).toContain('!/api/**');expect(config.navigationUrls).toContain('!/health/**');expect(config.assetGroups.every(group=>group.resources.files.every(path=>!path.includes('api')))).toBe(true);});
});
