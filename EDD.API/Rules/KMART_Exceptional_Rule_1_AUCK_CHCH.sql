-- KMART: AUCK → CHCH with freight payer 10098P — add 1 working day.
INSERT INTO public.edd_rules (
  id,
  name,
  priority,
  is_active,
  rule_type,
  rule_json,
  version,
  created_at
)
VALUES (
  gen_random_uuid(),
  'KMART Exceptional Rule #1 AUCK - CHCH +1',
  15,
  true,
  'definition',
  '{
    "conditions": [
      { "field": "origin", "operator": "equals", "value": "AUCK" },
      { "field": "destination", "operator": "equals", "value": "CHCH" },
      { "field": "freightPayer", "operator": "equals", "value": "10098P" }
    ],
    "actions": [
      { "type": "add_days", "value": 1 }
    ]
  }'::jsonb,
  '1',
  now()
);
