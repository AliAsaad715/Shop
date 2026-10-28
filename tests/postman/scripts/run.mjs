import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import newman from 'newman';
import { root } from './generate.mjs';

try
{
    const collection = JSON.parse(await readFile(resolve(root, 'shop.postman_collection.json'), 'utf8'));
    const environment = process.argv[2] ? JSON.parse(await readFile(resolve(process.argv[2]), 'utf8')) : undefined;
    const target = new URL(process.env.SHOP_BASE_URL ?? environment?.values?.find(value => value.key === 'baseUrl' && value.enabled !== false)?.value ?? 'http://localhost:8080');
    if (!['http:', 'https:'].includes(target.protocol) || target.username || target.password || target.search || target.hash || target.pathname !== '/')
    {
        throw new Error('baseUrl must be an HTTP(S) origin without credentials, query or path.');
    }
    if (process.env.SHOP_PROOF_ALLOW_WRITES !== '1')
    {
        throw new Error('This proof creates users, an image, a product and an order, then changes order status. Use a disposable database and set SHOP_PROOF_ALLOW_WRITES=1.');
    }
    // No JSON reporter: Newman reports can contain bearer tokens and request passwords.
    newman.run({
        collection, environment, folder: 'Security proof',
        envVar: [{ key: 'baseUrl', value: target.origin }],
        workingDir: root, insecureFileRead: false,
        iterationCount: 1, bail: ['failure', 'folder'],
        timeout: 120000, timeoutRequest: 10000, timeoutScript: 3000,
        reporters: ['cli'], reporter: { cli: { noConsole: true } },
    }, (error, summary) => {
        if (error)
        {
            console.error(error.message);
            process.exitCode = 1;
            return;
        }
        const expected = collection.item.find(item => item.name === 'Security proof').item.length;
        const requests = summary.run.stats.requests;
        const failures = summary.run.failures.length;
        if (failures || requests.failed || requests.total !== expected)
        {
            console.error(`Proof incomplete/failed: ${requests.total}/${expected} requests, ${failures} failure(s).`);
            process.exitCode = 1;
        }
    });
}
catch (error)
{
    console.error(error.message);
    process.exitCode = 1;
}
