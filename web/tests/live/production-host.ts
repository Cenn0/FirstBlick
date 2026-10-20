import {expect,Page} from '@playwright/test';

export function observeBrowserFailures(page:Page):string[]{
  const failures:string[]=[];
  page.on('pageerror',error=>failures.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&/Content Security Policy/i.test(message.text()))failures.push(message.text());});
  return failures;
}

/** The production host permits external scripts; global CSS must load without an inline activator. */
export async function verifyProductionStyles(page:Page):Promise<void>{
  await expect(page.locator('link[rel="stylesheet"][href^="styles-"]')).toHaveCount(1);
  await expect(page.locator('link[rel="stylesheet"][href^="styles-"]')).not.toHaveAttribute('media','print');
  await expect(page.locator('link[data-beasties-media],script:not([src])')).toHaveCount(0);
  await expect(page.getByRole('button',{name:'Anmelden',exact:false})).toHaveCSS('background-color','rgb(173, 83, 28)');
}
