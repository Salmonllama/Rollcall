// SPDX-License-Identifier: GPL-3.0-or-later
// Minimal SPDX expression evaluator: OR needs one allowed side, AND needs both, WITH keeps the base license.

/** @param {string} expression @param {Set<string>} allowed */
export function isAllowed(expression, allowed) {
  const tokens = expression.replace(/\*$/, '').match(/\(|\)|[^\s()]+/g) ?? [];
  let pos = 0;

  function primary() {
    const token = tokens[pos++];
    if (token === '(') {
      const value = or();
      pos++; // closing parenthesis
      return value;
    }
    let ok = token !== undefined && allowed.has(token.replace(/\+$/, ''));
    if (tokens[pos]?.toUpperCase() === 'WITH') pos += 2;
    return ok;
  }
  function and() {
    let value = primary();
    while (tokens[pos]?.toUpperCase() === 'AND') {
      pos++;
      value = primary() && value;
    }
    return value;
  }
  function or() {
    let value = and();
    while (tokens[pos]?.toUpperCase() === 'OR') {
      pos++;
      value = and() || value;
    }
    return value;
  }

  return tokens.length > 0 && or() && pos === tokens.length;
}
