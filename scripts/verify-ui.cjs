// Development-only visual checks. Pass an installed Playwright package path and Chromium executable.
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.argv[2]);
(async () => {
  const origin = process.env.HELPER_TEST_URL || 'http://127.0.0.1:8099';
  const browser = await chromium.launch({ headless:true, executablePath:process.argv[3] });
  try {
    const context = await browser.newContext({ viewport:{width:1440,height:1080}, deviceScaleFactor:1 });
    const page = await context.newPage();
    const errors=[];
    page.on('pageerror',error=>errors.push(error.message));
    await page.goto(origin);
    await page.locator('#login').waitFor({state:'visible'});
    fs.mkdirSync('artifacts/ui',{recursive:true});
    await page.screenshot({path:'artifacts/ui/login.png',fullPage:true});
    const account=JSON.parse(fs.readFileSync('artifacts/app/win-x64/data/account.json','utf8'));
    await page.locator('#username').fill(account.username);
    await page.locator('#password').fill(account.password);
    await page.locator('#login-form button').click();
    await page.locator('#console').waitFor({state:'visible'});
    await page.locator('#platform').filter({hasText:/Windows/}).waitFor();
    const settingsResponse=await context.request.get(origin+'/api/settings');
    const saved=(await settingsResponse.json()).settings;
    if(!saved.embyApiKey || await page.locator('#api-key').inputValue()!==saved.embyApiKey || await page.locator('#api-key').getAttribute('type')!=='text') throw new Error('API key must echo in plaintext after login');
    await page.reload();
    await page.locator('#console').waitFor({state:'visible'});
    if(await page.locator('#api-key').inputValue()!==saved.embyApiKey) throw new Error('API key missing after reload');
    // Mask only the screenshot; the real field remains plaintext for the user.
    const screenshot=async file=>page.screenshot({path:`artifacts/ui/${file}.png`,fullPage:true,mask:[page.locator('#api-key')],maskColor:'#e9e6fa'});
    await screenshot('overview');
    for(const name of ['language','media-features','chapters','infuse','diagnostics','logs','connection']) {
      await page.locator(`[data-page="${name}"]`).click();
      await page.locator('#page-'+name).waitFor({state:'visible'});
      await screenshot(name);
    }
    // Switches save on change; the value must survive a reload.
    await page.locator('[data-page="infuse"]').click();
    const previous=await page.locator('#enable-infuse').isChecked();
    await page.locator('#enable-infuse').setChecked(!previous);
    await page.locator('#toast').filter({hasText:'语言补写已'}).waitFor();
    await page.reload();
    await page.locator('#console').waitFor({state:'visible'});
    if(await page.locator('#enable-infuse').isChecked()===previous) throw new Error('Saved toggle did not survive reload');
    await page.locator('#enable-infuse').setChecked(previous);
    await page.locator('#toast').filter({hasText:'语言补写已'}).waitFor();
    await page.locator('[data-page="diagnostics"]').click();
    await page.locator('#item-id').fill('12');
    await page.locator('#inspect').click();
    await page.locator('.language-chip').first().waitFor();
    const audioTracks=page.locator('.track-group[data-type="Audio"] .language-chip');
    const subtitleTracks=page.locator('.track-group[data-type="Subtitle"] .language-chip');
    if(await audioTracks.count()!==7 || await subtitleTracks.count()!==12) throw new Error('Expected 7 audio and 12 subtitle tracks for the test disc');
    if(await page.locator('.track-group[data-type="Unknown"]').count()) throw new Error('Test disc has unclassified tracks');
    if(await page.locator('.track-language').filter({hasText:'中文'}).count()!==11) throw new Error('Chinese track names missing');
    await page.screenshot({path:'artifacts/ui/disc-inspected.png',fullPage:true});
    await page.setViewportSize({width:390,height:844});
    if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth)) throw new Error('Mobile disc inspector overflows horizontally');
    await page.screenshot({path:'artifacts/ui/disc-inspected-mobile.png',fullPage:true});
    await page.locator('[data-page="overview"]').click();
    if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth)) throw new Error('Mobile page overflows horizontally');
    await page.screenshot({path:'artifacts/ui/mobile.png',fullPage:true});
    if(errors.length) throw new Error('Browser errors: '+errors.join('; '));
    await page.locator('[data-page="connection"]').click();
    await page.locator('#account-form').scrollIntoViewIfNeeded();
    if(await page.locator('#account-username').inputValue()!==account.username) throw new Error('Account editor must show current username');
    if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth)) throw new Error('Mobile account editor overflows horizontally');
    await screenshot('account-mobile');
    await page.setViewportSize({width:1440,height:1080});
    await page.locator('#account-form').scrollIntoViewIfNeeded();
    await screenshot('account');
    // Exercise account submission without changing the user's credentials.
    await page.locator('#account-password').fill(account.password);
    await page.locator('#save-account').click();
    await page.locator('#login').waitFor({state:'visible'});
    if(await page.locator('#username').inputValue()!==account.username) throw new Error('Login must remember saved username');
    await page.locator('#password').fill(account.password);
    await page.locator('#login-form button').click();
    await page.locator('#console').waitFor({state:'visible'});
    if(await page.locator('#api-key').inputValue()!==saved.embyApiKey) throw new Error('Account save must preserve Emby API key');
    console.log('Passed browser checks: username/password form login, saved plaintext API key after reload, account save and re-login, all eight views, switch saved on change and persisted, 7 audio / 12 subtitle tracks, Chinese language names, mobile layout; no JavaScript errors.');
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
