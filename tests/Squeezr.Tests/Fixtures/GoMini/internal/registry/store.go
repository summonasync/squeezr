package registry

import (
	"strings"
)

type Widget struct {
	store *Store
}

func (w Widget) Label() string {
	return strings.ToLower(w.name)
}

func (r *Registry) Lookup(key string) string {
	var scratch struct {
		value string
	}
	type window struct {
		edges int
	}
	var view window
	return r.pick(key) + scratch.value + view.edges
}

func (*Registry) All() []string {
	return nil
}

func (r *Registry) pick(key string) int {
	return len(key)
}

type Store struct {
	items map[string]Item
}

func (s *Store) Put(key string) error {
	s.items[key] = Item{}
	return nil
}

type Item struct {
	raw string
}
