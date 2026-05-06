-- Rural delivery: add 1 working day when request.isRural is true.
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
  'Rural Delivery +1 day',
  20,
  true,
  'definition',
  '{
    "conditions": [
      { "field": "isRural", "operator": "equals", "value": "true" }
    ],
    "actions": [
      { "type": "add_days", "value": 1 }
    ]
  }'::jsonb,
  '1',
  now()
);


