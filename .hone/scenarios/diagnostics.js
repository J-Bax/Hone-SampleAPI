import http from 'k6/http';
import { JSON_PARAMS, createScenarioContext, mergeCatalog, tryJson } from './shared.js';

export function prepareScenario(name, options = {}) {
  const context = createScenarioContext(name);
  const response = invokeDiagnostic(context, 'prepare', options.sweepPrefix !== false);
  return mergeCatalog(context, response.catalog);
}

export function cleanupScenario(context, options = {}) {
  return invokeDiagnostic(context, 'cleanup', options.sweepPrefix === true);
}

function invokeDiagnostic(context, action, includeSweepPrefix) {
  const payload = {
    runId: context.runId,
    scenario: context.scenarioName,
    scopePrefix: context.scopePrefix,
  };

  if (includeSweepPrefix) {
    payload.sweepPrefix = context.familyPrefix;
  }

  const response = http.post(
    `${context.baseUrl}/diag/runs/${action}`,
    JSON.stringify(payload),
    JSON_PARAMS,
  );

  if (response.status !== 200) {
    throw new Error(`${action} failed (${response.status}): ${response.body}`);
  }

  return tryJson(response, null, {});
}
